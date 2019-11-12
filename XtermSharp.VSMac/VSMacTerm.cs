using System;
using System.Runtime.InteropServices;
using CoreFoundation;
using MonoDevelop.Components;
using MonoDevelop.Ide.Gui;
using XtermSharp.Mac;

namespace XtermSharp.VSMac {
	public class VSMacTerm : MonoDevelop.Ide.Gui.PadContent
	{
		readonly TerminalControl terminal;
		public VSMacTerm()
		{
			terminal = new TerminalControl();
			//this.Window.Content.Window.
		}
		protected override void Initialize (IPadWindow window)
		{
			base.Initialize (window);
                }
		
		public override Control Control => terminal;
	}

	class TerminalControl : Control
	{
		readonly TerminalView terminalView;
		int pid, fd;
		byte [] readBuffer = new byte [4 * 1024];

		protected override object CreateNativeWidget<T>()
		{
			return terminalView;
		}

		public TerminalControl()
		{
			
			terminalView = new TerminalView (new CoreGraphics.CGRect (0, 0, 1200, 300));
			var t = terminalView.Terminal;
			var size = new UnixWindowSize ();
			GetSize (t, ref size);

			pid = Pty.ForkAndExec ("/bin/bash", new string [] { "/bin/bash" }, Terminal.GetEnvironmentVariables (), out fd, size);
			DispatchIO.Read (fd, (nuint)readBuffer.Length, DispatchQueue.CurrentQueue, ChildProcessRead);


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
				Console.WriteLine (res);
			};
		}

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
				throw new Exception ("Error writing data to child");
			}
		}
	}
}
