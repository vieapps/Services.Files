using System;
using System.Threading;
using System.Runtime.InteropServices;

using net.vieapps.Components.Utility;

namespace net.vieapps.Services.Files
{
	class Program
	{
		internal static ServiceComponent Component = null;

		static void Main(string[] args)
		{
			// prepare
			Console.OutputEncoding = System.Text.Encoding.UTF8;
			Program.Component = new ServiceComponent();

			// set flag to run or exit (when called from API Gateway)
			EventWaitHandle waitHandle = null;
			bool isCalledFromAPIGateway = false, isCalledFromAPIGatewayToStop = false;
			if (args != null)
				for (var index = 0; index < args.Length; index++)
					if (args[index].IsStartsWith("/agc:"))
					{
						isCalledFromAPIGateway = true;
						isCalledFromAPIGatewayToStop = args[index].IsEquals("/agc:s");
						break;
					}

			// check to see if request to exit or not
			if (isCalledFromAPIGateway)
			{
				waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, "VIEApps.Services." + Program.Component.ServiceName.GetCapitalizedFirstLetter(), out bool createdNew);
				if (!createdNew)
					waitHandle.Set();

				// call to stop
				if (isCalledFromAPIGatewayToStop)
				{
					Program.Component.Dispose();
					return;
				}
			}

			// start the component
			Program.Component.Start(args);

			// waiting right here
			if (isCalledFromAPIGateway)
			{
				waitHandle.WaitOne();
				Program.Exit();
			}
			else
			{
				Program.ConsoleEventHandler = new ConsoleEventDelegate(Program.ConsoleEventCallback);
				Program.SetConsoleCtrlHandler(Program.ConsoleEventHandler, true);
				Console.WriteLine("=====> Press RETURN to terminate...");
				Console.ReadLine();
			}
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