﻿#light
namespace Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Tagging
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Language.Intellisense
open Microsoft.VisualStudio.Text.Classification
open Microsoft.VisualStudio.Utilities
open System.ComponentModel.Composition
open System.Collections.Generic

type internal DisplayWindowBroker 
    ( 
        _textView : ITextView,
        _completionBroker : ICompletionBroker,
        _signatureBroker : ISignatureHelpBroker,
        _smartTagBroker : ISmartTagBroker,
        _quickInfoBroker : IQuickInfoBroker)  =
    interface IDisplayWindowBroker with
        member x.TextView = _textView
        member x.IsCompletionActive = _completionBroker.IsCompletionActive(_textView)
        member x.IsSignatureHelpActive = _signatureBroker.IsSignatureHelpActive(_textView)
        member x.IsQuickInfoActive = _quickInfoBroker.IsQuickInfoActive(_textView)
        member x.IsSmartTagSessionActive = 
            if _smartTagBroker.IsSmartTagActive(_textView) then
                _smartTagBroker.GetSessions(_textView) 
                |> Seq.filter (fun x -> x.State = SmartTagState.Expanded) 
                |> SeqUtil.isNotEmpty
            else
                false
        member x.DismissDisplayWindows() =
            if _completionBroker.IsCompletionActive(_textView) then
                _completionBroker.DismissAllSessions(_textView)
            if _signatureBroker.IsSignatureHelpActive(_textView) then
                _signatureBroker.DismissAllSessions(_textView)
            if _quickInfoBroker.IsQuickInfoActive(_textView) then
                _quickInfoBroker.GetSessions(_textView) |> Seq.iter (fun x -> x.Dismiss())

[<Export(typeof<IDisplayWindowBrokerFactoryService>)>]
type internal DisplayWindowBrokerFactoryService
    [<ImportingConstructor>]
    (
        _completionBroker : ICompletionBroker,
        _signatureBroker : ISignatureHelpBroker,
        _smartTagBroker : ISmartTagBroker,
        _quickInfoBroker : IQuickInfoBroker ) = 

    interface IDisplayWindowBrokerFactoryService with
        member x.CreateDisplayWindowBroker textView = 
            let broker = DisplayWindowBroker(textView, _completionBroker, _signatureBroker, _smartTagBroker, _quickInfoBroker)
            broker :> IDisplayWindowBroker

/// This is the type responsible for tracking a line + column across edits to the
/// underlying ITextBuffer.  In a perfect world this would be implemented as an 
/// ITrackingSpan so we didn't have to update the locations on every single 
/// change to the ITextBuffer.  
///
/// Unfortunately the limitations of the ITrackingSpaninterface prevent us from doing 
/// that. One strategy you might employ is to say you'll track the Span which represents
/// the extent of the line you care about.  This works great right until you consider
/// the case where the line break before your Span is deleted.  Because you can't access 
/// the full text of that ITextVersion you can't "find" the new front of the line
/// you are tracking (same happens with the end).
///
/// So for now we're stuck with updating these on every edit to the ITextBuffer.  Not
/// ideal but will have to do for now.  
type internal TrackingLineColumn 
    ( 
        _textBuffer : ITextBuffer,
        _column : int,
        _mode : LineColumnTrackingMode,
        _onClose : TrackingLineColumn -> unit ) =

    /// This is the SnapshotSpan of the line that we are tracking.  It is None in the
    /// case of a deleted line
    let mutable _line : ITextSnapshotLine option  = None

    /// When the line this TrackingLineColumn is deleted, this will record the version 
    /// number of the last valid version containing the line.  That way if we undo this 
    /// can become valid again
    let mutable _lastValidVersion : (int * int) option  = None

    member x.TextBuffer = _textBuffer

    member x.Line 
        with get() = _line
        and set value = _line <- value

    member x.Column = _column

    member x.SurviveDeletes = 
        match _mode with
        | LineColumnTrackingMode.Default -> false
        | LineColumnTrackingMode.SurviveDeletes -> true

    member private x.VirtualSnapshotPoint = 
        match _line with
        | None -> None
        | Some line -> Some (VirtualSnapshotPoint(line, _column))

    /// Update the internal tracking information based on the new ITextSnapshot
    member x.UpdateForChange (e : TextContentChangedEventArgs) =
        let newSnapshot = e.After
        let changes = e.Changes

        // We have a valid line.  Need to update against this set of changes
        let withValidLine (oldLine : ITextSnapshotLine) =

            // For whatever reason this is now invalid.  Store the last good information so we can
            // recover during an undo operation
            let makeInvalid () = 
                _line <- None
                _lastValidVersion <- Some (oldLine.Snapshot.Version.VersionNumber, oldLine.LineNumber)

            // Is this change a delete of the entire line 
            let isLineDelete (change : ITextChange) = 
                change.LineCountDelta < 0 &&
                change.OldSpan.Contains(oldLine.ExtentIncludingLineBreak.Span)

            if (not x.SurviveDeletes) && Seq.exists isLineDelete e.Changes then
                // If this shouldn't survive a full line deletion and there is a deletion
                // then we are invalid
                makeInvalid()
            else

                // Calculate the line number delta for this given change. All we care about here
                // is the line number.  So changes line shortening the line don't matter for 
                // us.
                let getLineNumberDelta (change : ITextChange) =
                    if change.LineCountDelta = 0 || change.OldPosition >= oldLine.Start.Position then
                        // If there is no line change or this change occurred after the start 
                        // of our line then there is nothing to process
                        0
                    else 
                        // The change occurred before our line and there is a delta.  This is the 
                        // delta we need to apply to our line number
                        change.LineCountDelta

                // Calculate the line delta
                let delta = 
                    e.Changes
                    |> Seq.map getLineNumberDelta
                    |> Seq.sum
                let number = oldLine.LineNumber + delta
                match SnapshotUtil.TryGetLine newSnapshot number with
                | None -> makeInvalid()
                | Some line -> _line <- Some line

        // This line was deleted at some point in the past and hence we're invalid.  If the 
        // current change is an Undo back to the last version where we were valid then we
        // become valid again
        let checkUndo lastVersion lastLineNumber = 
            let newVersion = e.AfterVersion
            if newVersion.ReiteratedVersionNumber = lastVersion && lastLineNumber <= newSnapshot.LineCount then 
                _line <- Some (newSnapshot.GetLineFromLineNumber(lastLineNumber))
                _lastValidVersion <- None

        match _line, _lastValidVersion with
        | Some line, _ -> withValidLine line
        | None, Some (version,lineNumber) -> checkUndo version lineNumber
        | _ -> ()

    override x.ToString() =
        match x.VirtualSnapshotPoint with
        | Some(point) ->
            let line,_ = SnapshotPointUtil.GetLineColumn point.Position
            sprintf "%d,%d - %s" line _column (point.ToString())
        | None -> "Invalid"

    interface ITrackingLineColumn with
        member x.TextBuffer = _textBuffer
        member x.TrackingMode = _mode
        member x.VirtualPoint = x.VirtualSnapshotPoint
        member x.Point = 
            match x.VirtualSnapshotPoint with
            | None -> None
            | Some point -> Some point.Position
        member x.Close () =
            _onClose x
            _line <- None
            _lastValidVersion <- None

type internal TrackedData = {
    List : WeakReference<TrackingLineColumn> list
    Observer : System.IDisposable 
}

[<Export(typeof<ITrackingLineColumnService>)>]
type internal TrackingLineColumnService() = 
    
    let _map = new Dictionary<ITextBuffer, TrackedData>()

    /// Gets the data for the passed in buffer.  This method takes care of removing all 
    /// collected WeakReference items and updating the internal map 
    member private x.GetData textBuffer foundFunc notFoundFunc =
        let found,data = _map.TryGetValue(textBuffer)
        if not found then notFoundFunc()
        else
            let tlcs = 
                data.List 
                |> Seq.ofList 
                |> Seq.choose (fun weakRef -> weakRef.Target)
            if tlcs |> Seq.isEmpty then
                data.Observer.Dispose()
                _map.Remove(textBuffer) |> ignore
                notFoundFunc()
            else
                foundFunc data.Observer tlcs data.List

    /// Remove the TrackingLineColumn from the map.  If it is the only remaining 
    /// TrackingLineColumn assigned to the ITextBuffer, remove it from the map
    /// and unsubscribe from the Changed event
    member private x.Remove (tlc:TrackingLineColumn) = 
        let found (data:System.IDisposable) items rawList = 
            let items = items |> Seq.filter (fun cur -> cur <> tlc)
            if items |> Seq.isEmpty then 
                data.Dispose()
                _map.Remove(tlc.TextBuffer) |> ignore
            else
                let items = [Util.CreateWeakReference tlc] @ rawList
                _map.Item(tlc.TextBuffer) <- { Observer = data; List = items }
        x.GetData tlc.TextBuffer found (fun () -> ())

    /// Add the TrackingLineColumn to the map.  If this is the first item in the
    /// map then subscribe to the Changed event on the buffer
    member private x.Add (tlc:TrackingLineColumn) =
        let textBuffer = tlc.TextBuffer
        let found data _ list =
            let list = [Util.CreateWeakReference tlc] @ list
            _map.Item(textBuffer) <- { Observer = data; List = list }
        let notFound () =
            let observer = textBuffer.Changed |> Observable.subscribe x.OnBufferChanged
            let data = { List = [Util.CreateWeakReference tlc]; Observer = observer }
            _map.Add(textBuffer,data)
        x.GetData textBuffer found notFound

    member private x.OnBufferChanged (e:TextContentChangedEventArgs) = 
        let found _ (items: TrackingLineColumn seq) _ =
            items |> Seq.iter (fun tlc -> tlc.UpdateForChange e)
        x.GetData e.Before.TextBuffer found (fun () -> ())

    member x.Create (textBuffer:ITextBuffer) lineNumber column mode = 
        let tlc = TrackingLineColumn(textBuffer, column, mode, x.Remove)
        let tss = textBuffer.CurrentSnapshot
        let line = tss.GetLineFromLineNumber(lineNumber)
        tlc.Line <-  Some line
        x.Add tlc
        tlc

    interface ITrackingLineColumnService with
        member x.Create textBuffer lineNumber column mode = x.Create textBuffer lineNumber column mode :> ITrackingLineColumn
        member x.CloseAll() =
            let values = _map.Values |> List.ofSeq
            values 
            |> Seq.ofList
            |> Seq.map (fun data -> data.List)
            |> Seq.concat
            |> Seq.choose (fun item -> item.Target)
            |> Seq.map (fun tlc -> tlc :> ITrackingLineColumn)
            |> Seq.iter (fun tlc -> tlc.Close() )

/// Component which monitors commands across IVimBuffer instances and 
/// updates the LastCommand value for repeat purposes
[<Export(typeof<IVimBufferCreationListener>)>]
type internal ChangeTracker
    [<ImportingConstructor>]
    (
        _textChangeTrackerFactory : ITextChangeTrackerFactory,
        _vim : IVim
    ) =

    let _vimData = _vim.VimData

    member x.OnVimBufferCreated (buffer : IVimBuffer) =
        let handler = x.OnCommandRan buffer
        buffer.NormalMode.CommandRunner.CommandRan |> Event.add handler
        buffer.VisualLineMode.CommandRunner.CommandRan |> Event.add handler
        buffer.VisualBlockMode.CommandRunner.CommandRan |> Event.add handler
        buffer.VisualCharacterMode.CommandRunner.CommandRan |> Event.add handler

        let tracker = _textChangeTrackerFactory.GetTextChangeTracker buffer
        tracker.ChangeCompleted |> Event.add (x.OnTextChanged buffer)

    member x.OnCommandRan buffer (data : CommandRunData) = 
        let command = data.CommandBinding
        if command.IsMovement || command.IsSpecial then
            // Movement and special commands don't participate in change tracking
            ()
        elif command.IsRepeatable then 
            _vimData.LastCommand <- StoredCommand.OfCommand data.Command data.CommandBinding |> Some
        else 
            _vimData.LastCommand <- None

    member x.OnTextChanged buffer data = 
        let textChange = StoredCommand.TextChangeCommand data
        let useCurrent() = 
            _vimData.LastCommand <- Some textChange

        let maybeLink (command : StoredCommand) = 
            if Util.IsFlagSet command.CommandFlags CommandFlags.LinkedWithNextTextChange then
                let change = StoredCommand.LinkedCommand (command, textChange)
                _vimData.LastCommand <- Some change
            else 
                useCurrent()

        match _vimData.LastCommand with
        | None -> useCurrent()
        | Some storedCommand -> maybeLink storedCommand

    interface IVimBufferCreationListener with
        member x.VimBufferCreated buffer = x.OnVimBufferCreated buffer


