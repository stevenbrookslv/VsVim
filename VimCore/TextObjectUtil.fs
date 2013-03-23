﻿#light

namespace Vim

open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open System.Collections.Generic
open Vim.Modes
open Vim.StringBuilderExtensions
open Vim.Interpreter

[<RequireQualifiedAccess>]
type internal SentenceKind = 

    /// Default behavior of a sentence as defined by ':help sentence'
    | Default

    /// There is one definition of a sentence in Vim but the implementation indicates there
    /// are actually 2.  In some cases the trailing characters are not considered a part of 
    /// a sentence.
    ///
    /// http://groups.google.com/group/vim_use/browse_thread/thread/d3f28cf801dc2030
    | NoTrailingCharacters

[<RequireQualifiedAccess>]
type internal SectionKind =

    /// By default a section break happens on a form feed in the first 
    /// column or one of the nroff macros
    | Default

    /// Split on an open brace in addition to default settings
    | OnOpenBrace

    /// Split on a close brace in addition to default settings
    | OnCloseBrace

    /// Split on an open brace or below a close brace in addition to default
    /// settings
    | OnOpenBraceOrBelowCloseBrace

/// This key is used on a perf sensitive path.  Using a simple tuple causes the 
/// hashing mechanism to be boxing.  This shows up heavily on profiles hence we
/// use a custom type here to avoid the allocation when using as a key
[<Struct>]
[<CustomEquality>]
[<NoComparison>]
type SentenceKey 
    (
        _sentenceKind : SentenceKind,
        _position : int
    ) =

    member x.SentenceKind = _sentenceKind

    member x.Position = _position

    member x.Equals (other : SentenceKey) =
        x.EqualsCore other

    member x.EqualsCore (other : SentenceKey) = 
        _sentenceKind = other.SentenceKind && _position = other.Position
    
    override x.Equals(obj) =
        match obj with
        | :? SentenceKey as other -> x.EqualsCore other
        | _ -> false

    override x.GetHashCode() = _position

    interface System.IEquatable<SentenceKey> with
        member x.Equals other = x.EqualsCore other

type internal TextObjectUtil
    (
        _globalSettings : IVimGlobalSettings,
        _textBuffer : ITextBuffer
    ) = 

    let mutable _versionCached = _textBuffer.CurrentSnapshot.Version.VersionNumber
    let _isSentenceEndMap = Dictionary<SentenceKey, bool>()
    let _isSentenceStartMap = Dictionary<SentenceKey, bool>()

    /// Set of characters which represent the end of a sentence. 
    static let SentenceEndChars = ['.'; '!'; '?']

    /// Set of characters which can validly follow a sentence 
    static let SentenceTrailingChars = [')';']'; '"'; '\'']

    /// Current ITextSnapshot for the ITextBuffer
    member x.CurrentSnapshot = _textBuffer.CurrentSnapshot

    member x.CheckCache() = 
        let version = _textBuffer.CurrentSnapshot.Version.VersionNumber
        if version <> _versionCached then
            _isSentenceEndMap.Clear()
            _isSentenceStartMap.Clear()
            _versionCached <- version

    /// Is this line a blank line with no blank lines above it 
    member x.IsBlankLineWithNoBlankAbove line = 
        if 0 = SnapshotLineUtil.GetLength line then
            let number = line.LineNumber - 1
            match SnapshotUtil.TryGetLine line.Snapshot number with
            | None -> true
            | Some line -> line.Length <> 0
        else
            false

    /// Is this point the start of a paragraph.  Considers both paragraph and section 
    /// start points
    member x.IsParagraphStart (column : SnapshotColumn) =
        if column.IsStartOfLine then
            let line = column.Line
            x.IsParagraphStartOnly line || x.IsSectionStart SectionKind.Default line
        else
            false

    /// Is this point the start of a paragraph.  This does not consider section starts but
    /// specifically items related to paragraphs.  Paragraphs begin after a blank line or
    /// at one of the specified macros
    member x.IsParagraphStartOnly line = 
        let startPoint = SnapshotLineUtil.GetStart line

        SnapshotPointUtil.IsStartPoint startPoint ||
        x.IsTextMacroMatchLine line _globalSettings.Paragraphs ||
        x.IsBlankLineWithNoBlankAbove line

    /// Is this line the start of a section.  Section boundaries can only occur at the
    /// start of a line or in a couple of scenarios around braces
    member x.IsSectionStart kind line = 

        // Is this a standard section start
        let startPoint = SnapshotLineUtil.GetStart line
        let isStandardStart = 
            if SnapshotPointUtil.IsStartPoint startPoint then
                true
            elif SnapshotPointUtil.IsChar '\f' startPoint then
                true
            else
                x.IsTextMacroMatchLine line _globalSettings.Sections

        if isStandardStart then
            true
        else
            match kind with
            | SectionKind.Default ->
                false
            | SectionKind.OnOpenBrace -> 
                SnapshotPointUtil.IsChar '{' startPoint
            | SectionKind.OnCloseBrace -> 
                SnapshotPointUtil.IsChar '}' startPoint
            | SectionKind.OnOpenBraceOrBelowCloseBrace -> 
                if SnapshotPointUtil.IsChar '{' startPoint then
                    true
                else
                    match SnapshotUtil.TryGetLine line.Snapshot (line.LineNumber - 1) with
                    | None -> false
                    | Some line -> SnapshotPointUtil.IsChar '}' line.Start

    /// Is this the end point of the span of an actual sentence.  Considers sentence, paragraph and 
    /// section semantics
    member x.IsSentenceEnd sentenceKind (column : SnapshotColumn) = 
        x.CheckCache()

        // The IsSentenceEnd function is used heavily on a given ITextSnapshot to determine information
        // about a sentence.  Profiling shows that it does contribute significantly to performance in
        // large file scenarios.  Caching the result here saves significantly on perf
        let key = SentenceKey(sentenceKind, column.Point.Position)
        let mutable value = false
        if _isSentenceEndMap.TryGetValue(key, &value) then
            value
        else
            let value = x.IsSentenceEndCore sentenceKind column 
            _isSentenceEndMap.Add(key, value)
            value

    member x.IsSentenceEndCore sentenceKind (column : SnapshotColumn) = 

        // Is the char for the provided point in the given list.  Make sure to 
        // account for the snapshot end point here as it makes the remaining 
        // logic easier 
        let isCharInList list point = 
            match SnapshotPointUtil.TryGetChar point with
            | None -> false
            | Some c -> ListUtil.contains c list 

        // Is this a valid white space item to end the sentence
        let isWhiteSpaceEnd point = 
            SnapshotPointUtil.IsWhiteSpace point || 
            SnapshotPointUtil.IsInsideLineBreak point ||
            SnapshotPointUtil.IsEndPoint point

        // Is it SnapshotPoint pointing to an end char which actually ends
        // the sentence
        let isEndNoTrailing point = 
            if isCharInList SentenceEndChars point then
                point |> SnapshotPointUtil.AddOne |> isWhiteSpaceEnd
            else
                false

        // Is it a trailing character which properly ends a sentence
        let isTrailing point = 
            if isCharInList SentenceTrailingChars point then

                // Need to see if we are preceded by an end character first
                let isPrecededByEnd = 
                    if SnapshotPointUtil.IsStartPoint point then 
                        false
                    else
                        point 
                        |> SnapshotPointUtil.SubtractOne
                        |> SnapshotPointUtil.GetPointsIncludingLineBreak Path.Backward
                        |> Seq.skipWhile (isCharInList SentenceTrailingChars)
                        |> SeqUtil.tryHeadOnly
                        |> Option.map (isCharInList SentenceEndChars)
                        |> OptionUtil.getOrDefault false

                if isPrecededByEnd then
                    point |> SnapshotPointUtil.AddOne |> isWhiteSpaceEnd
                else
                    false
            else
                false

        /// Is this line a sentence line?  A sentence line is a sentence which is caused by
        /// a paragraph, section boundary or blank line
        let isSentenceLine line =
            x.IsTextMacroMatchLine line _globalSettings.Paragraphs ||
            x.IsTextMacroMatchLine line _globalSettings.Sections ||
            x.IsBlankLineWithNoBlankAbove line

        // Is this the last point on a sentence line?  The last point on the sentence line is the 
        // final character of the line break.  This is key to understand: the line break on a sentence
        // line is not considered white space!!!  This can be verified by placing the caret on the 'b' in 
        // the following and doing a 'yas'
        //
        //  a
        //  
        //  b
        let isSentenceLineLast point =
            let line = SnapshotPointUtil.GetContainingLine point
            SnapshotLineUtil.IsLastPointIncludingLineBreak line point && isSentenceLine line

        let line = column.Line
        if column.IsStartOfLine && isSentenceLine line then
            // If this point is the start of a sentence line then it's the end point of the
            // previous sentence span. 
            true
        else
            match SnapshotPointUtil.TrySubtractOne column.Point with
            | None -> 
                // Start of buffer is not the end of a sentence
                false 
            | Some point -> 
                match sentenceKind with
                | SentenceKind.Default -> isEndNoTrailing point || isTrailing point || isSentenceLineLast point
                | SentenceKind.NoTrailingCharacters -> isEndNoTrailing point || isSentenceLineLast point

    /// Is this point the star of a sentence.  Considers sentences, paragraphs and section
    /// boundaries
    member x.IsSentenceStart sentenceKind (column : SnapshotColumn) = 
        if x.IsSentenceStartOnly sentenceKind column.Point then
            true
        else
            if column.IsStartOfLine then
                let line = column.Line
                x.IsParagraphStartOnly line || x.IsSectionStart SectionKind.Default line
            else
                false

    /// Is the start of a sentence.  This doesn't consider section or paragraph boundaries
    /// but specifically items related to the start of a sentence
    member x.IsSentenceStartOnly sentenceKind (point : SnapshotPoint) = 
        x.CheckCache()

        let key = SentenceKey(sentenceKind, point.Position)
        let mutable value = false
        if _isSentenceStartMap.TryGetValue(key, &value) then
            value
        else
            let value = x.IsSentenceStartOnlyCore sentenceKind point
            _isSentenceStartMap.Add(key, value)
            value

    /// Is the start of a sentence.  This doesn't consider section or paragraph boundaries
    /// but specifically items related to the start of a sentence
    member x.IsSentenceStartOnlyCore sentenceKind point = 

        let snapshot = SnapshotPointUtil.GetSnapshot point

        // Get the point after the last sentence
        let priorEndColumn = 
            point
            |> SnapshotColumnUtil.GetColumnsIncludingLineBreak Path.Backward
            |> Seq.filter (x.IsSentenceEnd sentenceKind)
            |> SeqUtil.tryHeadOnly

        match priorEndColumn with 
        | None -> 
            // No prior end point so the start of this sentence is the start of the 
            // ITextBuffer
            SnapshotPointUtil.IsStartPoint point 
        | Some priorEndColumn -> 
            // Move past the white space until we get the start point.  Don't need 
            // to consider line breaks because we are only dealing with sentence 
            // specific items.  Methods like IsSentenceStart deal with line breaks
            // by including paragraphs
            let startPoint =
                priorEndColumn
                |> SnapshotColumnUtil.GetPoint 
                |> SnapshotPointUtil.GetPoints Path.Forward
                |> Seq.skipWhile SnapshotPointUtil.IsWhiteSpaceOrInsideLineBreak
                |> SeqUtil.headOrDefault (SnapshotUtil.GetEndPoint x.CurrentSnapshot)

            startPoint.Position = point.Position
    
    /// This function is used to match nroff macros for both section and paragraph sections.  It 
    /// determines if the line starts with the proper macro string
    member x.IsTextMacroMatch point macroString =
        let line = SnapshotPointUtil.GetContainingLine point
        if line.Start <> point then
            // Match can only occur at the start of the line
            false
        else
            x.IsTextMacroMatchLine line macroString

    /// This function is used to match nroff macros for both section and paragraph sections.  It 
    /// determines if the line starts with the proper macro string
    member x.IsTextMacroMatchLine (line : ITextSnapshotLine) macroString =
        let isLengthCorrect = 0 = (String.length macroString) % 2
        if not isLengthCorrect then 
            // Match can only occur with a valid macro string length
            false
        elif line.Length = 0 || line.Start.GetChar() <> '.' then
            // Line must start with a '.' 
            false
        elif line.Length = 2 then
            // Line is in the form '.H' so can only match pairs which have a blank 
            let c = line.Start |> SnapshotPointUtil.AddOne |> SnapshotPointUtil.GetChar
            { 0 .. ((String.length macroString) - 1)}
            |> Seq.filter (fun i -> i % 2 = 0)
            |> Seq.exists (fun i -> macroString.[i] = c && macroString.[i + 1] = ' ')
        elif line.Length = 3 then
            // Line is in the form '.HH' so match both items
            let c1 = line.Start |> SnapshotPointUtil.AddOne |> SnapshotPointUtil.GetChar
            let c2 = line.Start |> SnapshotPointUtil.Add 2 |> SnapshotPointUtil.GetChar
            { 0 .. ((String.length macroString) - 1)}
            |> Seq.filter (fun i -> i % 2 = 0)
            |> Seq.exists (fun i -> macroString.[i] = c1 && macroString.[i + 1] = c2)
        else
            // Line can't match
            false

    /// Get the SnapshotSpan values for the paragraph object starting from the given SnapshotPoint
    /// in the specified direction.  
    member x.GetParagraphs path point = 

        // Get the full span of a paragraph given a start point
        let getFullSpanFromStartColumn (column : SnapshotColumn) =
            Contract.Assert (x.IsParagraphStart column)
            let point = column.Point
            let snapshot = SnapshotPointUtil.GetSnapshot point
            let endPoint =
                point 
                |> SnapshotPointUtil.AddOne
                |> SnapshotColumnUtil.GetColumnsIncludingLineBreak Path.Forward
                |> Seq.skipWhile (fun c -> not (x.IsParagraphStart c))
                |> Seq.map SnapshotColumnUtil.GetPoint
                |> SeqUtil.headOrDefault (SnapshotUtil.GetEndPoint snapshot)
            SnapshotSpan(point, endPoint)

        x.GetTextObjectsCore point path x.IsParagraphStart getFullSpanFromStartColumn

    /// Get the SnapshotLineRange values for the section values starting from the given SnapshotPoint 
    /// in the specified direction.  Note: The full span of the section will be returned if the 
    /// provided SnapshotPoint is in the middle of it
    member x.GetSectionRanges sectionKind path point =

        let isNotSectionStart line = not (x.IsSectionStart sectionKind line)

        // Get the ITextSnapshotLine which is the start of the nearest section from the given point.  
        let snapshot = SnapshotPointUtil.GetSnapshot point
        let getSectionStartBackward point = 
            let line = SnapshotPointUtil.GetContainingLine point
            SnapshotUtil.GetLines snapshot line.LineNumber Path.Backward
            |> Seq.skipWhile isNotSectionStart
            |> SeqUtil.headOrDefault (SnapshotUtil.GetFirstLine snapshot)

        // Get the SnapshotLineRange from an ITextSnapshotLine which begins a section
        let getSectionRangeFromStart (startLine: ITextSnapshotLine) =
            Contract.Assert (x.IsSectionStart sectionKind startLine)
            let nextStartLine = 
                SnapshotUtil.GetLines snapshot startLine.LineNumber Path.Forward
                |> SeqUtil.skipMax 1
                |> Seq.skipWhile isNotSectionStart
                |> SeqUtil.tryHeadOnly

            let count = 
                match nextStartLine with 
                | None -> (SnapshotUtil.GetLastLineNumber snapshot) - startLine.LineNumber + 1
                | Some nextStartLine -> nextStartLine.LineNumber - startLine.LineNumber

            SnapshotLineRangeUtil.CreateForLineAndCount startLine count |> Option.get

        match path with
        | Path.Forward ->
            // Start the search from the nearest section start backward
            let startLine = getSectionStartBackward point

            Seq.unfold (fun point -> 
                if SnapshotPointUtil.IsEndPoint point then
                    // Once we hit <end> we are done
                    None
                else
                    let startLine = SnapshotPointUtil.GetContainingLine point
                    let range = getSectionRangeFromStart startLine
                    Some (range, range.EndIncludingLineBreak)) startLine.Start
        | Path.Backward ->

            // Create the sequence by doing an unfold.  The provided point will be the 
            // start point of the following section SnapshotLineRange
            let getPrevious point =
                match SnapshotPointUtil.TrySubtractOne point with
                | None -> 
                    // Once we are at start we are done
                    None
                | Some point ->
                    let endLine = SnapshotPointUtil.GetContainingLine point
                    let startLine = getSectionStartBackward point
                    let range = SnapshotLineRangeUtil.CreateForLineRange startLine endLine
                    Some (range, range.Start)

            // There is a bit of a special case with backward sections.  If the point is 
            // anywhere on the line of a section start then really it starts from the start
            // of the line
            let point = 
                let line = SnapshotPointUtil.GetContainingLine point
                if x.IsSectionStart sectionKind line then
                    line.Start
                else 
                    point

            let startLine = getSectionStartBackward point
            let sections = Seq.unfold getPrevious startLine.Start

            if startLine.Start = point then
                // The provided SnapshotPoint is the first point on a section so we don't include
                // it and the sections sequence as stands is correct
                sections
            else 
                // Need to include the section which spans this point 
                let added = startLine |> getSectionRangeFromStart |> Seq.singleton
                Seq.append added sections

    /// Get the SnapshotSpan values for the section values starting from the given SnapshotPoint 
    /// in the specified direction.  Note: The full span of the section will be returned if the 
    /// provided SnapshotPoint is in the middle of it
    member x.GetSections sectionKind path point =
        x.GetSectionRanges sectionKind path point
        |> Seq.map (fun range -> range.ExtentIncludingLineBreak)

    /// Get the SnapshotSpan values for the sentence values starting from the given SnapshotPoint 
    /// in the specified direction.  Note: The full span of the section will be returned if the 
    /// provided SnapshotPoint is in the middle of it
    member x.GetSentences sentenceKind path (point : SnapshotPoint) =

        // Get the full span of a sentence given the particular start point
        let getFullSpanFromStartColumn (column : SnapshotColumn) =
            Contract.Assert (x.IsSentenceStart sentenceKind column)

            let point = column.Point
            let snapshot = column.Snapshot

            // Move forward until we hit the end point and then move one past it so the end
            // point is included in the span
            let endPoint = 
                point
                |> SnapshotPointUtil.AddOne
                |> SnapshotColumnUtil.GetColumnsIncludingLineBreak Path.Forward
                |> Seq.skipWhile (fun c -> not (x.IsSentenceEnd sentenceKind c))
                |> Seq.map (fun c -> c.Point)
                |> SeqUtil.headOrDefault (SnapshotUtil.GetEndPoint snapshot)
            SnapshotSpan(point, endPoint)

        x.GetTextObjectsCore point path (x.IsSentenceStart sentenceKind) getFullSpanFromStartColumn

    /// Get the text objects core from the given point in the given direction
    member x.GetTextObjectsCore (point : SnapshotPoint) path (isStartColumn : SnapshotColumn -> bool) (getSpanFromStartColumn : SnapshotColumn -> SnapshotSpan) = 

        let column = SnapshotColumnUtil.CreateFromPoint point
        let snapshot = SnapshotPointUtil.GetSnapshot point
        let isNotStartColumn column = not (isStartColumn column)

        // Wrap the get full span method to deal with <end>.  
        let getSpanFromStartColumn (column : SnapshotColumn) = 
            if SnapshotPointUtil.IsEndPoint column.Point then
                SnapshotSpan(column.Point, 0)
            else
                getSpanFromStartColumn column

        // Get the section start going backwards from the given point.
        let getStartBackward point = 
            point 
            |> SnapshotColumnUtil.GetColumnsIncludingLineBreak Path.Backward
            |> Seq.skipWhile isNotStartColumn
            |> Seq.map SnapshotColumnUtil.GetPoint
            |> SeqUtil.headOrDefault (SnapshotPoint(snapshot, 0))

        // Get the section start going forward from the given point 
        let getStartForward point = 
            point
            |> SnapshotColumnUtil.GetColumnsIncludingLineBreak Path.Forward
            |> Seq.skipWhile isNotStartColumn
            |> Seq.map SnapshotColumnUtil.GetPoint
            |> SeqUtil.headOrDefault (SnapshotUtil.GetEndPoint snapshot)

        // Search includes the section which contains the start point so go ahead and get it
        match path, SnapshotPointUtil.IsStartPoint point with 
        | Path.Forward, _ ->

            // Get the next object.  The provided point should either be <end> or point 
            // to the start of a section
            let getNext point = 
                if SnapshotPointUtil.IsEndPoint point then
                    None
                else
                    let column = SnapshotColumn(point)
                    let span = getSpanFromStartColumn column
                    let startPoint = getStartForward span.End
                    Some (span, startPoint)

            // Get the point to start the sequence from.  Need to be very careful though 
            // because it's possible for the first SnapshotSpan being completely before the 
            // provided SnapshotPoint.  This can happen for text objects which have white 
            // space chunks and the provided point 
            let startPoint = 
                let startPoint = getStartBackward point
                let startColumn = SnapshotColumn(startPoint)
                let span = getSpanFromStartColumn startColumn
                if point.Position >= span.End.Position then
                    getStartForward span.End
                else
                    startPoint
            Seq.unfold getNext startPoint
        | Path.Backward, true ->
            // Handle the special case here
            Seq.empty
        | Path.Backward, false ->

            // Get the previous section.  The provided point should either be 0 or point
            // to the start of a section
            let getPrevious point = 
                if SnapshotPointUtil.IsStartPoint point then
                    None
                else
                    let startPoint = point |> SnapshotPointUtil.SubtractOne |> getStartBackward
                    let startColumn = SnapshotColumn(startPoint)
                    let span = getSpanFromStartColumn startColumn
                    Some (span, startPoint)

            Seq.unfold getPrevious point
