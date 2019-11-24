using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using AppKit;
using CoreFoundation;
using CoreGraphics;
using Microsoft.VisualStudio.Text.Editor;
using MonoDevelop.Components;
using MonoDevelop.Components.Commands;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Commands;
using MonoDevelop.Ide.Fonts;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide.Gui.Content;
using XtermSharp.Mac;
using Xwt;
using Xwt.Drawing;
using Xwt.Mac;
namespace XtermSharp.VSMac {
	public static class Util {
		public static NSColor ToNSColor (this Xwt.Drawing.Color col)
		{
			return NSColor.FromDeviceRgba ((float)col.Red, (float)col.Green, (float)col.Blue, (float)col.Alpha);
		}
	}

	public class VSMacTerm : MonoDevelop.Ide.Gui.PadContent
	{
		readonly TerminalControl terminal;
		public VSMacTerm()
		{
			terminal = new TerminalControl();
		}

		protected override void Initialize (IPadWindow window)
		{
			base.Initialize (window);
		}

		public override Control Control => terminal;

		[CommandHandler (ViewCommands.ZoomIn)]
		public void ZoomIn () { 
                
			var editorFont = IdeServices.FontService.GetFont ("Editor");
			var font = NSFont.FromFontName (editorFont.Family,  (nfloat)editorFont.Size);
		}

		//  [<CommandHandler ("MonoDevelop.Ide.Commands.ViewCommands.ZoomOut")>]
		//member x.ZoomOut () = x.Editor.GetContent<IZoomable>().ZoomOut ()

		//  [<CommandHandler ("MonoDevelop.Ide.Commands.ViewCommands.ZoomReset")>]
		//member x.ZoomReset () = x.Editor.GetContent<IZoomable>().ZoomReset ()
	}

	class TerminalPadView : TerminalView {
		private CGSize size;

		public TerminalPadView (CGRect rect, TerminalViewOptions options) : base (rect, options)
		{
		}

		public override void SetFrameSize (CGSize newSize)
		{
			base.SetFrameSize (newSize);
                        if (size != newSize) {
				size = newSize;
				var rect = new CGRect (0, 0, newSize.Width, newSize.Height);
				base.Frame = rect;
			} else {
				size = newSize;
			}
		}
		public override CGRect Frame { get => base.Frame; set => base.Frame = value; }
	}

	class TerminalControl : Control
	{
		readonly TerminalPadView terminalView;
		int pid, fd;
		byte [] readBuffer = new byte [4 * 1024];

		protected override object CreateNativeWidget<T>()
		{
			return terminalView;
		}

		NSFont GetEditorFont ()
		{
			var editorFont = IdeServices.FontService.GetFont ("Editor");
			return NSFont.FromFontName (editorFont.Family, (nfloat)editorFont.Size);
		}

		void SetFonts(TerminalViewOptions options)
		{
			var editorFont = IdeServices.FontService.GetFont ("Editor");
			var font = NSFont.FromFontName (editorFont.Family, (nfloat)editorFont.Size);
			options.Font = font;
			//TODO:
			options.FontBold = font;
			options.FontBoldItalic = font;
			options.FontItalic = font;
		}

		public TerminalControl()
		{
			//TODO: This is a massive hack... we should not be using ActiveDocument events
			var zoomable = IdeApp.Workbench.ActiveDocument.GetContent<ICocoaTextView> ();
			
			var options = new TerminalViewOptions ();
			SetFonts (options);
			//options.Font = GetEditorFont ();
			var bgColor = MonoDevelop.Ide.Gui.Styles.BackgroundColor.ToNSColor ();
			var fgColor = MonoDevelop.Ide.Gui.Styles.BaseForegroundColor.ToNSColor ();
			options.BackgroundColor = bgColor;
			options.ForegroundColor = fgColor;
			terminalView = new TerminalPadView (new CGRect (0, 0, 1200, 300), options);


			var t = terminalView.Terminal;
			var size = new UnixWindowSize ();
			GetSize (t, ref size);

			pid = Pty.ForkAndExec ("/bin/bash", new string [] { "/bin/bash" }, Terminal.GetEnvironmentVariables (), out fd, size);
			DispatchIO.Read (fd, (nuint)readBuffer.Length, DispatchQueue.CurrentQueue, ChildProcessRead);

	    //TODO: This is a massive hack... we should not be using ActiveDocument events
	    if(zoomable != null)
            zoomable.ZoomLevelChanged += (sender, args) =>
            {
                var font = GetEditorFont();
                SetFonts(options);
                var newFont = NSFont.FromFontName(font.FontName, (int)(font.PointSize * (nfloat)(args.NewZoomLevel / 100)));
                options.Font = newFont;
                options.FontBold = newFont;
                options.FontBoldItalic = newFont;
                options.FontItalic = newFont;
                //terminalView.FontNormal = newFont;
                terminalView.ComputeCellDimensions();
                var newsize = new UnixWindowSize();
                GetSize(terminalView.Terminal, ref newsize);
                terminalView.Terminal.Resize(newsize.col, newsize.row);
                var res = Pty.SetWinSize(fd, ref newsize);

                //terminalView.UpdateDisplay();
                //terminalView.FullBufferUpdate();
                //terminalView.QueuePendingDisplay();
		// this strange looking line is needed as the .Frame setter
		// recalculates how many rows/columns can fit inside the frame
		// based on frame dimensions and cell dimensions
                terminalView.Frame = terminalView.Frame;
            };

            terminalView.UserInput += (byte [] data) => {
				DispatchIO.Write (fd, DispatchData.FromByteBuffer (data), DispatchQueue.CurrentQueue, ChildProcessWrite);
			};
			terminalView.Feed ("Welcome to XtermSharp - NSView frontend!\n");
			//terminalView.TitleChanged += (TerminalView sender, string title) => {
			//	View.Window.Title = title;
			//};
			terminalView.SizeChanged += (newCols, newRows) => {
				UnixWindowSize nz = new UnixWindowSize ();
				GetSize (t, ref nz);
				var res = Pty.SetWinSize (fd, ref nz);
				//Console.WriteLine (res);
			};
		}

		//public override void ViewDidLayout (
		//{
		//	base.ViewDidLayout ();
		//	terminalView.Frame = View.Frame;
		//}

		void GetSize (Terminal terminal, ref UnixWindowSize size)
		{
			size = new UnixWindowSize () {
				col = (short)terminal.Cols,
				row = (short)terminal.Rows,
				xpixel = 1200,
				ypixel = 300
			};
		}

		static int x;
		void ChildProcessRead (DispatchData data, int error)
		{
			using (var map = data.CreateMap (out var buffer, out var size)) {
				// Faster, but harder to debug:
				// terminalView.Feed (buffer, (int) size);
				//Console.WriteLine ("Read {0} bytes", size);
				if (size == 0) {
					//View.Window.Close ();
					return;
				}
				byte [] copy = new byte [(int)size];
				Marshal.Copy (buffer, copy, 0, (int)size);

				//System.IO.File.WriteAllBytes ("/tmp/log-" + (x++), copy);
				terminalView.Feed (copy);
			}
			DispatchIO.Read (fd, (nuint)readBuffer.Length, DispatchQueue.CurrentQueue, ChildProcessRead);
		}

		void ChildProcessWrite (DispatchData left, int error)
		{
			if (error != 0) {
				//throw new Exception ("Error writing data to child");
			}
		}
	}
}
