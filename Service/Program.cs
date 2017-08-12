using System;
using System.Runtime.InteropServices;

namespace net.vieapps.Services.Files
{
	class Program
	{
		internal static ServiceComponent Component = null;

		static void Main(string[] args)
		{
			// start the component
			Program.Component = new ServiceComponent();
			Program.Component.Start(args);

			// handle the closing events
			Program.ConsoleEventHandler = new ConsoleEventDelegate(Program.ConsoleEventCallback);
			Program.SetConsoleCtrlHandler(Program.ConsoleEventHandler, true);

			// wait
			while (true) { }
		}

		internal static void Exit()
		{
			Program.Component.Dispose();
			Environment.Exit(0);
		}

		#region Closing event handler
		static bool ConsoleEventCallback(int eventCode)
		{
			switch (eventCode)
			{
				case 0:		// Ctrl + C
				case 1:		// Ctrl + Break
				case 2:		// Close
				case 6:        // Shutdown
					Program.Exit();
					break;
			}
			return false;
		}

		static ConsoleEventDelegate ConsoleEventHandler;   // Keeps it from getting garbage collected

		// Pinvoke
		private delegate bool ConsoleEventDelegate(int eventCode);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);
		#endregion

	}
}