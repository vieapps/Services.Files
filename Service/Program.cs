#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	class Program
	{
		internal static ServiceComponent Component = null;
		internal static bool AsService = false;

		static void Main(string[] args)
		{
			// get flag to run or stop (when called from API Gateway)
			var apiCall = args?.FirstOrDefault(a => a.IsStartsWith("/agc:"));
			var apiCallToStop = apiCall != null && apiCall.IsEquals("/agc:s");
			Program.AsService = apiCall != null;

			// initialize the instance of service component
			Program.Component = new ServiceComponent();

			// prepare the signal to start/stop
			EventWaitHandle waitHandle = null;
			if (Program.AsService)
			{
				// get the flag of the existing instance
				waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, Program.Component.ServiceURI, out bool createdNew);

				// process the call to stop
				if (apiCallToStop)
				{
					// raise an event to stop current existing instance
					if (!createdNew)
						waitHandle.Set();

					// then exit
					Program.Component.Dispose();
					return;
				}
			}

			// start the service component
			if (!Program.AsService)
			{
				Console.OutputEncoding = System.Text.Encoding.UTF8;
				Console.WriteLine("Starting the service [" + Program.Component.ServiceURI + "]");
				Console.WriteLine("=====> Press RETURN to terminate...");
			}
			Program.Component.Start(args);

			// wait for exit
			if (Program.AsService)
			{
				waitHandle.WaitOne();
				Program.Component.Dispose();
			}
			else
			{
				Program.ConsoleEventHandler = new ConsoleEventDelegate(Program.ConsoleEventCallback);
				Program.SetConsoleCtrlHandler(Program.ConsoleEventHandler, true);
				Console.ReadLine();
			}
		}

		#region Closing event handler
		static bool ConsoleEventCallback(int eventCode)
		{
			switch (eventCode)
			{
				case 0:        // Ctrl + C
				case 1:        // Ctrl + Break
				case 2:        // Close
				case 6:        // Shutdown
					Program.Component.Dispose();
					break;
			}
			return false;
		}

		// keeps it from getting garbage collected
		static ConsoleEventDelegate ConsoleEventHandler;

		// invokes
		private delegate bool ConsoleEventDelegate(int eventCode);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);
		#endregion

	}
}