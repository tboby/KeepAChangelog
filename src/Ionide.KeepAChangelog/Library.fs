﻿namespace Ionide.KeepAChangelog

module Domain =
    open SemVersion
    open System

    type ChangelogData =
        { Added: string list
          Changed: string list
          Deprecated: string list
          Removed: string list
          Fixed: string list
          Security: string list
          Custom: Map<string, string list>}
        static member Default =
            { Added = []
              Changed = []
              Deprecated = []
              Removed = []
              Fixed = []
              Security = []
              Custom = Map.empty }

    type Changelogs =
        { Unreleased: ChangelogData option
          Releases: (SemanticVersion * DateTime * ChangelogData option) list }

module Parser =


    open Domain
    open FParsec
    open FParsec.CharParsers
    open FParsec.Primitives
    open System.IO
    open System.Collections.Generic

    type Parser<'t> = Parser<'t, unit>

    let pipe6 p1 p2 p3 p4 p5 p6 fn =
        parse {
            let! a = p1
            let! b = p2
            let! c = p3
            let! d = p4
            let! e = p5
            let! f = p6
            return fn a b c d e f
        }

    let pipe7 p1 p2 p3 p4 p5 p6 p7 fn =
        parse {
            let! a = p1
            let! b = p2
            let! c = p3
            let! d = p4
            let! e = p5
            let! f = p6
            let! g = p7
            return fn a b c d e f g
        }

    let skipTillStringOrEof str : Parser<unit, _> =
        fun stream ->
            let mutable found = false

            stream.SkipCharsOrNewlinesUntilString(str, System.Int32.MaxValue, &found)
            |> ignore

            Reply(())

    let pBullet: Parser<char> = (pchar '-' <|> pchar '*') <?> "bullet"

    let pEntry: Parser<string> =
        let bullet = (pBullet .>> spaces1)

        let content =
            // we need to parse all of this line, sure
            // but we also need to keep parsing next lines until
            // * we find a bullet, or
            // * we get an empty line
            let firstLine = FParsec.CharParsers.restOfLine true

            let followingLine =
                nextCharSatisfiesNot (fun c -> c = '\n' || c = '-' || c = '*')
                >>. spaces
                >>. FParsec.CharParsers.restOfLine true

            let rest = many followingLine

            pipe2 firstLine rest (fun f rest -> String.concat " " (f :: rest))
            <?> "line item"

        pipe2 bullet content (fun bullet text -> $"{bullet} {text}")

    let pCustomSection: Parser<string * string list> =
        let sectionName =
            skipString "###"
             >>. spaces1
             >>. restOfLine true // TODO: maybe not the whole line?
             <?> $"custom section header"
        sectionName
        .>>. (many pEntry <?> $"{sectionName} entries")
        .>> newline

    let pSection sectionName : Parser<string list> =
        (skipString "###"
         >>. spaces1
         >>. skipString sectionName)
        <?> $"{sectionName} section header"
        >>. newline
        >>. (many pEntry <?> $"{sectionName} entries")
        .>> newline

    let pAdded = pSection "Added"
    let pChanged = pSection "Changed"
    let pRemoved = pSection "Removed"
    let pDeprecated = pSection "Deprecated"
    let pFixed = pSection "Fixed"
    let pSecurity = pSection "Security"
    let pOrEmptyList p = opt (attempt p)

    // TODO: this requires this exact ordering, revisit later
    let pSections: Parser<ChangelogData -> ChangelogData> =
        choice [
            attempt (pAdded |>> fun x data -> { data with Added = x })
            attempt (pChanged |>> fun x data -> { data with Changed = x })
            attempt (pRemoved |>> fun x data -> { data with Removed = x })
            attempt (pDeprecated |>> fun x data -> { data with Deprecated = x })
            attempt (pFixed |>> fun x data -> { data with Fixed = x })
            attempt (pSecurity |>> fun x data -> { data with Security = x })
            attempt (many1 pCustomSection |>> fun x data -> { data with Custom = Map.ofList x })
        ]

    let pData: Parser<ChangelogData, unit> =
        many1 pSections
        |>> List.fold (fun x f -> f x) ChangelogData.Default

    let pHeader: Parser<unit> =
        (skipString "# Changelog" >>. skipNewline
         .>> skipTillStringOrEof "##")
        <?> "Changelog header"

    let pUnreleased: Parser<ChangelogData option, unit> =
        let name =
            skipString "##"
            >>. spaces1
            >>. (skipString "Unreleased"
                 <|> skipString "[Unreleased")
            .>> skipRestOfLine true
            <?> "Unreleased label"

        name .>> opt (many newline) >>. opt pData
        <?> "Unreleased version section"


    let validSemverChars =
        [| for c in '0' .. '9' -> c
           for c in 'A' .. 'Z' -> c
           for c in 'a' .. 'z' -> c
           yield '-'
           yield '.'
           yield '+' |]
        |> Set.ofArray

    let pSemver: Parser<_> =
        many1Chars (satisfy validSemverChars.Contains)
        |>> fun text -> SemVersion.SemanticVersion.Parse text

    let pDate: Parser<_> =
        let pYear =
            pipe4 digit digit digit digit (fun y1 y2 y3 y4 -> System.Int32.Parse $"{y1}{y2}{y3}{y4}")

        let pMonth = pipe2 digit digit (fun m1 m2 -> System.Int32.Parse $"{m1}{m2}")

        let pDay = pipe2 digit digit (fun d1 d2 -> System.Int32.Parse $"{d1}{d2}")

        pipe5 pYear (pchar '-') pMonth (pchar '-') pDay (fun y _ m _ d -> System.DateTime(y, m, d))

    let mdUrl inner =
        let linkText = between (pchar '[') (pchar ']') inner

        let linkTarget = between (pchar '(') (pchar ')') (skipMany1Till anyChar (pchar ')'))

        linkText .>> opt linkTarget

    let pVersion = mdUrl pSemver <|> pSemver

    let pRelease: Parser<SemVersion.SemanticVersion * System.DateTime * ChangelogData option> =
        let vPart = skipString "##" >>. spaces1 >>. pVersion
        let middle = spaces1 .>> pchar '-' .>> spaces1
        let date = pDate .>> skipRestOfLine true

        pipe5 vPart middle date (opt (many newline)) (opt pData) (fun v _ date _ data -> v, date, data)

    let pChangeLogs: Parser<Changelogs, unit> =
        pipe3
            pHeader
            (pUnreleased
             |>> fun unreleased ->
                     match unreleased with
                     | None -> None
                     | Some u when u = ChangelogData.Default -> None
                     | Some unreleased -> Some unreleased)
            (attempt (many pRelease))
            (fun header unreleased releases ->
                { Unreleased = unreleased
                  Releases = releases })

    let parseChangeLog (file: FileInfo) =
        match
            runParserOnFile
                pChangeLogs
                ()
                file.FullName
                (System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier = false))
            with
        | ParserResult.Success (result, _, pos) -> Result.Ok result
        | ParserResult.Failure (msg, structuredError, pos) -> Result.Error(msg, structuredError)