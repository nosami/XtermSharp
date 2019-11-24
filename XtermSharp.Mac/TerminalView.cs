using System;
using Foundation;
using CoreGraphics;
using AppKit;
using CoreText;
using ObjCRuntime;
using System.Text;
using System.Collections.Generic;
using XtermSharp;
using CoreFoundation;

namespace XtermSharp.Mac {
	public class TerminalViewOptions {
		public NSFont Font { get; set; } = NSFont.FromFontName ("Menlo", 14);
		public NSFont FontItalic { get; set; } = NSFont.FromFontName ("Menlo Bold", 14);
		public NSFont FontBold { get; set; } = NSFont.FromFontName ("Menlo Bold", 14);
		public NSFont FontBoldItalic { get; set; } = NSFont.FromFontName ("Menlo Bold", 14);
		public NSColor ForegroundColor { get; set; } = NSColor.White;
		public NSColor BackgroundColor { get; set; } = NSColor.Black;
	}

	/// <summary>
	/// An AppKit Terminal View.
	/// </summary>
	public class TerminalView : NSView, INSTextInputClient, INSUserInterfaceValidations, ITerminalDelegate {
		static CGAffineTransform textMatrix;

		Terminal terminal;
		CircularList<NSAttributedString> buffer;
		//NSFont fontNormal, fontItalic, fontBold, fontBoldItalic;
		NSView caret, debug;
		
		nfloat cellHeight, cellWidth, cellDelta;

		public bool HasFocus { get; private set; }

		public TerminalView (CGRect rect, TerminalViewOptions options) : base (rect)
		{
			this.options = options;
			
			
			//fontNormal = options.Font;
			//fontBold = options.FontBold;
			//fontItalic = options.FontItalic;
			//fontBoldItalic = options.FontBoldItalic;
			ComputeCellDimensions ();

			var cols = (int)(rect.Width / cellWidth);
			var rows = (int)(rect.Height / cellHeight);

			terminal = new Terminal (this, new TerminalOptions () { Cols = cols, Rows = rows });
			FullBufferUpdate ();
			
			caret = new NSView (new CGRect (0, cellDelta, cellHeight, cellWidth)) {
				WantsLayer = true
			};
			AddSubview (caret);
			debug = new NSView (new CGRect (0, 0, 10, 10)) {
				WantsLayer = true
			};
			//AddSubview (debug);

			var caretColor = NSColor.FromColor (NSColor.Blue.ColorSpace, 0.4f, 0.2f, 0.9f, 0.5f);

			caret.Layer.BackgroundColor = caretColor.CGColor;

			debug.Layer.BackgroundColor = caretColor.CGColor;
		}

		/// <summary>
		/// Gets the Terminal object that is being used for this terminal
		/// </summary>
		/// <value>The terminal.</value>
		public Terminal Terminal => terminal;

		/// <summary>
		/// Gets or sets a value indicating whether this <see cref="T:XtermSharp.Mac.TerminalView"/> treats the "Alt/Option" key on the mac keyboard as a meta key,
		/// which has the effect of sending ESC+letter when Meta-letter is pressed.   Otherwise, it passes the keystroke that MacOS provides from the OS keyboardmc
		/// .
		/// </summary>
		/// <value><c>true</c> if option acts as a meta key; otherwise, <c>false</c>.</value>
		public bool OptionAsMetaKey { get; set; } = true;

		public void ComputeCellDimensions ()
		{
			//var style = new NSMutableParagraphStyle ();
			//style.LineSpacing = 40;
			//style.MinimumLineHeight = 40;
			//style.LineHeightMultiple = 2;

			//var attributedString = new NSMutableAttributedString ("W", new CTStringAttributes () { Font = fontNormal });
			//attributedString.AddAttribute (CTStringAttributeKey.ParagraphStyle, style, new NSRange (0, attributedString.Length));
			//	new NSAttributedString ("W", new NSStringAttributes () { Font = fontNormal })
			//res.AddAttribute (CTStringAttributeKey.ParagraphStyle, style, new NSRange (0, res.Length));
			var line = new CTLine (new NSAttributedString ("W", new NSStringAttributes () { Font = options.Font }));
			var bounds = line.GetBounds (CTLineBoundsOptions.UseOpticalBounds);
			cellWidth = bounds.Width;
			//cellHeight = bounds.Height + 5;
			cellHeight = (int)bounds.Height;
			cellDelta = bounds.Y;

			// We call ComputeCellDimensions when the font size is zoomed
			attributes.Clear ();
		}

		StringBuilder basBuilder = new StringBuilder ();

		NSColor [] colors = new NSColor [257];

		NSColor MapColor (int color, bool isFg)
		{
			// The default color
			if (color == Renderer.DefaultColor) {
				if (isFg)
					return options.ForegroundColor;
				else
					return options.BackgroundColor;
			} else if (color == Renderer.InvertedDefaultColor) {
				if (isFg)
					return options.BackgroundColor;
				else
					return options.ForegroundColor;
			}

			if (colors [color] == null) {
				Color tcolor = Color.DefaultAnsiColors [color];

				colors [color] = NSColor.FromCalibratedRgb (tcolor.Red / 255f, tcolor.Green / 255f, tcolor.Blue / 255f);
			}
			return colors [color];
		}

		Dictionary<int, NSStringAttributes> attributes = new Dictionary<int, NSStringAttributes> ();
		NSStringAttributes GetAttributes (int attribute)
		{
			// ((int)flags << 18) | (fg << 9) | bg;
			int bg = attribute & 0x1ff;
			int fg = (attribute >> 9) & 0x1ff;
			var flags = (FLAGS) (attribute >> 18);

			if (flags.HasFlag (FLAGS.INVERSE)) {
				var tmp = bg;
				bg = fg;
				fg = tmp;

				if (fg == Renderer.DefaultColor)
					fg = Renderer.InvertedDefaultColor;
				if (bg == Renderer.DefaultColor)
					bg = Renderer.InvertedDefaultColor;
			}

			if (attributes.TryGetValue (attribute, out var result))
				return result;

			NSFont font;
			if (flags.HasFlag (FLAGS.BOLD)) {
				if (flags.HasFlag (FLAGS.ITALIC))
					font = options.FontBoldItalic;
				else
					font = options.FontBold;
			} else if (flags.HasFlag (FLAGS.ITALIC))
				font = options.FontItalic;
			else
				font = options.Font;

			var paragraphStyle = new NSMutableParagraphStyle ();
			paragraphStyle.LineSpacing = 5;
			paragraphStyle.LineHeightMultiple = 2;
			var nsattr = new NSStringAttributes () { Font = font, ForegroundColor = MapColor (fg, true), BackgroundColor = MapColor (bg, false), ParagraphStyle = paragraphStyle };
			if (flags.HasFlag (FLAGS.UNDERLINE)) {
				nsattr.UnderlineColor = nsattr.ForegroundColor;
				nsattr.UnderlineStyle = (int) NSUnderlineStyle.Single;
		
			}
			attributes [attribute] = nsattr;
			return nsattr;
		}

		NSAttributedString BuildAttributedString (BufferLine line, int cols)
		{
			var res = new NSMutableAttributedString();
			
			int attr = 0;

			basBuilder.Clear ();

			
			for (int col = 0; col < cols; col++) {
				var ch = line [col];
				if (col == 0)
					attr = ch.Attribute;
				else {
					if (attr != ch.Attribute) {
						var attributedString = new NSAttributedString (basBuilder.ToString (), GetAttributes (attr));
						res.Append (attributedString);
						basBuilder.Clear ();
						attr = ch.Attribute;
					}
				}
				basBuilder.Append (ch.Code == 0 ? ' ' : (char)ch.Rune);
			}
			res.Append (new NSAttributedString (basBuilder.ToString (), GetAttributes (attr)));

			var style = new NSMutableParagraphStyle ();
			style.LineSpacing = 40;
			style.MinimumLineHeight = 40;

			style.LineHeightMultiple = 2;
			res.AddAttribute (NSStringAttributeKey.ParagraphStyle, style, new NSRange (0, res.Length));
			return res;
		}

		public void FullBufferUpdate ()
		{
			var rows = terminal.Rows;
			if (buffer == null)
				buffer = new CircularList<NSAttributedString> (terminal.Buffer.Lines.MaxLength);
			var cols = terminal.Cols;
			for (int row = 0; row < rows; row++)
				buffer [row] = BuildAttributedString (terminal.Buffer.Lines [row], cols);
		}

		void UpdateCursorPosition ()
		{
			caret.Frame = new CGRect (terminal.Buffer.X * cellWidth - 1, Frame.Height - cellHeight - (terminal.Buffer.Y * cellHeight - cellDelta - 1), cellWidth, cellHeight + 2);
		}

		public void UpdateDisplay ()
		{
			terminal.GetUpdateRange (out var rowStart, out var rowEnd);
			terminal.ClearUpdateRange ();
			var cols = terminal.Cols;
			var tb = terminal.Buffer;
			for (int row = rowStart; row <= rowEnd; row++) {
				var rowIndex = row + tb.YDisp;
				if(terminal.Buffer.Lines.Length > rowIndex) {
					buffer [rowIndex] = BuildAttributedString (terminal.Buffer.Lines [rowIndex], cols);
				}
			}
			//var baseLine = Frame.Height - cellDelta;
			// new CGPoint (0, baseLine - (cellHeight + row * cellHeight));
			UpdateCursorPosition ();

			// Should compute the rectangle instead
			//Console.WriteLine ($"Dirty range: {rowStart},{rowEnd}");
			var region = new CGRect (0, Frame.Height - cellHeight - (rowEnd * cellHeight - cellDelta - 1), Frame.Width, (cellHeight - cellDelta) * (rowEnd-rowStart+1));

			//debug.Frame = region;
			SetNeedsDisplayInRect (region);
			//Console.WriteLine ("Dirty rectangle: " + region);
			pendingDisplay = false;
		}

		// Flip coordinate system.
		//public override bool IsFlipped => true;

		// Simple tester API.
		public void Feed (string text)
		{
			terminal.Feed (Encoding.UTF8.GetBytes (text));
			QueuePendingDisplay ();
		}

		// 
		// The code below is intended to not repaint too often, which can produce flicker, for example
		// when the user refreshes the display, and this repains the screen, as dispatch delivers data
		// in blocks of 1024 bytes, which is not enough to cover the whole screen, so this delays
		// the update for a 1/600th of a secon.
		bool pendingDisplay;
		public void QueuePendingDisplay ()
		{
			// throttle
			if (!pendingDisplay) {
				pendingDisplay = true;
				DispatchQueue.CurrentQueue.DispatchAfter (new DispatchTime (DispatchTime.Now, 16670000*2), UpdateDisplay);
			}
		}

		public void Feed (byte [] text, int length = -1)
		{
			terminal.Feed (text, length);

			// The problem is calling UpdateDisplay here, because there is still data pending.
			QueuePendingDisplay ();
		}

		public void Feed (IntPtr buffer, int length)
		{
			terminal.Feed (buffer, length);
			QueuePendingDisplay ();
		}

		NSTrackingArea trackingArea;

		public override void CursorUpdate (NSEvent theEvent)
		    => NSCursor.IBeamCursor.Set ();

		void MakeFirstResponder ()
		{
			Window.MakeFirstResponder (this);
		}

		bool loadedCalled;
		internal event Action Loaded;
		public override CGRect Frame {
			get => base.Frame; set {
				var oldSize = base.Frame.Size;
				base.Frame = value;

				var newRows = (int) (value.Height / cellHeight);
				var newCols = (int) (value.Width / cellWidth);

				if (newCols != terminal.Cols || newRows != terminal.Rows) {
					terminal.Resize (newCols, newRows);
					FullBufferUpdate ();
				}

				UpdateCursorPosition ();
				// It might seem like this wrong place to call Loaded, and that
				// ViewDidMoveToSuperview might make more sense
				// but Editor code expects Loaded to be called after ViewportWidth and ViewportHeight are set
				if (!loadedCalled) {
					loadedCalled = true;
					Loaded?.Invoke ();
				}

				SizeChanged?.Invoke (newCols, newRows);
			}
		}

		/// <summary>
		///  This event is raised when the terminal size has change, due to a NSView frame changed.
		/// </summary>
		public event Action<int, int> SizeChanged;

		[Export ("validateUserInterfaceItem:")]
		bool INSUserInterfaceValidations.ValidateUserInterfaceItem (INSValidatedUserInterfaceItem item)
		{
			var selector = item.Action.Name;

			switch (selector) {
			case "performTextFinderAction:":
				switch ((NSTextFinderAction)(long)item.Tag) {
				case NSTextFinderAction.ShowFindInterface:
				case NSTextFinderAction.ShowReplaceInterface:
				case NSTextFinderAction.HideFindInterface:
				case NSTextFinderAction.HideReplaceInterface:
					return true;
				}
				return false;
			}

			Console.WriteLine ("Validating " + selector);
			return false;
		}

		[Export ("cut:")]
		void Cut (NSObject sender)
		{ }

		[Export ("copy:")]
		void Copy (NSObject sender)
		{ }

		[Export ("paste:")]
		void Paste (NSObject sender)
		{
		}

		[Export ("selectAll:")]
		void SelectAll (NSObject sender)
		{
		}

		[Export ("undo:")]
		void Undo (NSObject sender)
		{ }

		[Export ("redo:")]
		void Redo (NSObject sender)
		{
		}

		[Export ("zoomIn:")]
		void ZoomIn (NSObject sender)
		{ }

		[Export ("zoomOut:")]
		void ZoomOut (NSObject sender)
		{ }

		[Export ("zoomReset:")]
		void ZoomReset (NSObject sender)
		{ }


		#region Input / NSTextInputClient

		public override bool BecomeFirstResponder ()
		{

			var response = base.BecomeFirstResponder ();
			if (response) {
				HasFocus = true;
			}
			return response;
		}

		public override bool ResignFirstResponder ()
		{
			var response = base.ResignFirstResponder ();
			if (response) {
				HasFocus = false;
			}
			return response;
		}

		public override bool AcceptsFirstResponder ()
		    => true;

		public override void KeyDown (NSEvent theEvent)
		{
			var eventFlags = theEvent.ModifierFlags;

			// Handle Option-letter to send the ESC sequence plus the letter as expected by terminals
			if (eventFlags.HasFlag (NSEventModifierMask.AlternateKeyMask) && OptionAsMetaKey) {
				var rawCharacter = theEvent.CharactersIgnoringModifiers;
				Send (EscapeSequences.CmdEsc);
				Send (Encoding.UTF8.GetBytes (rawCharacter));
				return;
			} else if (eventFlags.HasFlag (NSEventModifierMask.ControlKeyMask)) {
				// Sends the control sequence
				var ch = theEvent.CharactersIgnoringModifiers;
				if (ch.Length == 1) {
					var d = Char.ToUpper (ch [0]);
					if (d >= 'A' && d <= 'Z')
						Send (new byte [] { (byte)(d - 'A' + 1) });
					return;
				} 
			} else if (eventFlags.HasFlag (NSEventModifierMask.FunctionKeyMask)) {
				var ch = theEvent.CharactersIgnoringModifiers;
				if (ch.Length == 1) {
					NSFunctionKey code = (NSFunctionKey)ch [0];
					switch (code) {
					case NSFunctionKey.F1:
						Send (EscapeSequences.CmdF [0]);
						break;
					case NSFunctionKey.F2:
						Send (EscapeSequences.CmdF [1]);
						break;
					case NSFunctionKey.F3:
						Send (EscapeSequences.CmdF [2]);
						break;
					case NSFunctionKey.F4:
						Send (EscapeSequences.CmdF [3]);
						break;
					case NSFunctionKey.F5:
						Send (EscapeSequences.CmdF [4]);
						break;
					case NSFunctionKey.F6:
						Send (EscapeSequences.CmdF [5]);
						break;
					case NSFunctionKey.F7:
						Send (EscapeSequences.CmdF [6]);
						break;
					case NSFunctionKey.F8:
						Send (EscapeSequences.CmdF [7]);
						break;
					case NSFunctionKey.F9:
						Send (EscapeSequences.CmdF [8]);
						break;
					case NSFunctionKey.F10:
						Send (EscapeSequences.CmdF [9]);
						break;
					case NSFunctionKey.F11:
						Send (EscapeSequences.CmdF [10]);
						break;
					case NSFunctionKey.F12:
						Send (EscapeSequences.CmdF [11]);
						break;
					case NSFunctionKey.Delete:
						Send (EscapeSequences.CmdDelKey);
						break;
					case NSFunctionKey.UpArrow:
						Send (EscapeSequences.MoveUpNormal);
						break;
					case NSFunctionKey.DownArrow:
						Send (EscapeSequences.MoveDownNormal);
						break;
					case NSFunctionKey.LeftArrow:
						Send (EscapeSequences.MoveLeftNormal);
						break;
					case NSFunctionKey.RightArrow:
						Send (EscapeSequences.MoveRightNormal);
						break;
					}
				}
				return;
			} 

			InterpretKeyEvents (new [] { theEvent });
		}

		[Export ("validAttributesForMarkedText")]
		public NSArray<NSString> ValidAttributesForMarkedText ()
		    => new NSArray<NSString> ();

		[Export ("insertText:replacementRange:")]
		public void InsertText (NSObject text, NSRange replacementRange)
		{
			if (text is NSString str) {
				var data = str.Encode (NSStringEncoding.UTF8);
				Send (data.ToArray ());
			}
			NeedsDisplay = true;
		}

		static NSRange notFoundRange = new NSRange (NSRange.NotFound, 0);

		[Export ("hasMarkedText")]
		public bool HasMarkedText ()
		{
			return false;
		}

		[Export ("markedRange")]
		public NSRange MarkedRange ()
		{
			return notFoundRange;
		}

		[Export ("setMarkedText:selectedRange:replacementRange:")]
		public void SetMarkedText (NSObject setMarkedText, NSRange selectedRange, NSRange replacementRange)
		{

		}

		void ProcessUnhandledEvent (NSEvent evt)
		{
			// Handle Control-letter
			if (evt.ModifierFlags.HasFlag (NSEventModifierMask.ControlKeyMask)) {
				
			}
		}

		// Invoked to raise input on the control, which should probably be sent to the actual child process or remote connection
		public Action<byte []> UserInput;

		public void Send (byte [] data)
		{
			UserInput?.Invoke (data);
		}

		[Export ("doCommandBySelector:")]
		public void DoCommandBySelector (Selector selector)
		{
			switch (selector.Name){
			case "insertNewline:":
				Send (EscapeSequences.CmdRet);
				break;
			case "cancelOperation:":
				Send (EscapeSequences.CmdEsc);
				break;
			case "deleteBackward:":
				Send (new byte [] { 0x7f });
				break;
			case "moveUp:":
				Send (terminal.ApplicationCursor ? EscapeSequences.MoveUpApp : EscapeSequences.MoveUpNormal);
				break;
			case "moveDown:":
				Send (terminal.ApplicationCursor ? EscapeSequences.MoveDownApp : EscapeSequences.MoveDownNormal);
				break;
			case "moveLeft:":
				Send (terminal.ApplicationCursor ? EscapeSequences.MoveLeftApp : EscapeSequences.MoveLeftNormal);
				break;
			case "moveRight:":
				Send (terminal.ApplicationCursor ? EscapeSequences.MoveRightApp : EscapeSequences.MoveRightNormal);
				break;
			case "insertTab:":
				Send (EscapeSequences.CmdTab);
				break;
			case "insertBackTab:":
				Send (EscapeSequences.CmdBackTab);
				break;
			case "moveToBeginningOfLine:":
				Send (terminal.ApplicationCursor ? EscapeSequences.MoveHomeApp : EscapeSequences.MoveHomeNormal);
				break;
			case "moveToEndOfLine:":
				Send (terminal.ApplicationCursor ? EscapeSequences.MoveEndApp : EscapeSequences.MoveEndNormal);
				break;
			case "noop:":
				ProcessUnhandledEvent (NSApplication.SharedApplication.CurrentEvent);
				break;

				// Here the semantics depend on app mode, if set, then we function as scroll up, otherwise the modifier acts as scroll up.
			case "pageUp:":
				if (terminal.ApplicationCursor)
					Send (EscapeSequences.CmdPageUp);
				else {
					// TODO: view should scroll one page up.
				}
				break;

			case "pageUpAndModifySelection":
				if (terminal.ApplicationCursor){
					// TODO: view should scroll one page up.
				}
				else
					Send (EscapeSequences.CmdPageUp);
				break;
			case "pageDown:":
				if (terminal.ApplicationCursor)
					Send (EscapeSequences.CmdPageDown);
				else {
					// TODO: view should scroll one page down
				}
				break;
			case "pageDownAndModifySelection:":
				if (terminal.ApplicationCursor) {
					// TODO: view should scroll one page up.
				} else
					Send (EscapeSequences.CmdPageDown);
				break;
			default:
				Console.WriteLine ("Unhandled key event: " + selector.Name);
				break;
			}
			
		}

		[Export ("selectedRange")]
		public NSRange SelectedRange => notFoundRange;

		[Export ("attributedSubstringForProposedRange:actualRange:")]
		public NSAttributedString AttributedSubstringForProposedRange (NSRange range, out NSRange actualRange)
		{
			actualRange = range;
			return null;
		}

		[Export ("firstRectForCharacterRange:")]
		public CGRect FirstRectForCharacterRange (NSRange range)
		{
			return FirstRectForCharacterRange (range, out var _);
		}

		[Export ("firstRectForCharacterRange:actualRange:")]
		public CGRect FirstRectForCharacterRange (NSRange range, out NSRange actualRange)
		{
			throw new NotImplementedException ();
		}

		#endregion

		int count;
		private readonly TerminalViewOptions options;

		public override void DrawRect (CGRect dirtyRect)
		{
			//Console.WriteLine ($"DrawRect: {dirtyRect}");
			options.BackgroundColor.Set ();
			NSGraphics.RectFill (dirtyRect);

			CGContext context = NSGraphicsContext.CurrentContext.GraphicsPort;
			//context.TextMatrix = textMatrix;

#if false
			var maxCol = terminal.Cols;
			var maxRow = terminal.Rows;

			for (int row = 0; row < maxRow; row++) {
				context.TextPosition = new CGPoint (0, 15 + row * 15);
				var str = "";
				for (int col = 0; col < maxCol; col++) {
					var ch = terminal.Buffer.Lines [row] [col];
					str += (ch.Code == 0) ? ' ' : (char)ch.Rune;
				}
				var ctline = new CTLine (new NSAttributedString (str, new NSStringAttributes () { Font = font }));
				
				ctline.Draw (context);
			}
#else
			var maxRow = terminal.Rows;
			var yDisp = terminal.Buffer.YDisp;
			var baseLine = Frame.Height - cellDelta;
			for (int row = 0; row < maxRow; row++) {
				context.TextPosition = new CGPoint (0, baseLine - (cellHeight + row * cellHeight));
				var attrLine = buffer [row + yDisp];
				if (attrLine == null)
					continue;
				var ctline = new CTLine (attrLine);

				ctline.Draw (context);
			}
#endif
		}

		void ITerminalDelegate.ShowCursor (Terminal terminal)
		{
		}

		public event Action<TerminalView, string> TitleChanged;

		void ITerminalDelegate.SetTerminalTitle (Terminal source, string title)
		{
			if (TitleChanged != null)
				TitleChanged (this, title);
		}

		void ITerminalDelegate.SizeChanged (Terminal source)
		{
			throw new NotImplementedException ();
		}

		void ComputeMouseEvent (NSEvent theEvent, bool down, out int buttonFlags, out int col, out int row)
		{
			var point = theEvent.LocationInWindow;
			col = (int)(point.X / cellWidth);
			row = (int)((Frame.Height - point.Y) / cellHeight);

			Console.WriteLine ($"Mouse at {col},{row}");
			var flags = theEvent.ModifierFlags;

			buttonFlags = terminal.EncodeButton (
				(int)theEvent.ButtonNumber, release: false,
				shift: flags.HasFlag (NSEventModifierMask.ShiftKeyMask),
				meta: flags.HasFlag (NSEventModifierMask.AlternateKeyMask),
				control: flags.HasFlag (NSEventModifierMask.ControlKeyMask));
		}

		void SharedMouseEvent (NSEvent theEvent, bool down)
		{
			ComputeMouseEvent (theEvent, down, out var buttonFlags, out var col, out var row);
			terminal.SendEvent (buttonFlags, col, row);
		}

		public override void MouseDown (NSEvent theEvent)
		{
			if (!terminal.MouseEvents)
				return;

			SharedMouseEvent (theEvent, down: true);

		}

		public override void MouseUp (NSEvent theEvent)
		{
			if (!terminal.MouseEvents)
				return;

			if (terminal.MouseSendsRelease)
				SharedMouseEvent (theEvent, down: false);
		}

		public override void MouseDragged (NSEvent theEvent)
		{
			if (!terminal.MouseEvents)
				return;

			if (terminal.MouseSendsAllMotion || terminal.MouseSendsMotionWhenPressed) {
				ComputeMouseEvent (theEvent, true, out var buttonFlags, out var col, out var row);
				terminal.SendMotion (buttonFlags, col, row);
			}
		}


	}
}
