﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Ome
{
    public partial class App : Application
    {
        private const string PipeName = "OmeAudioAppPipe";
        private Mutex _mutex;
        public bool NoFocus { get; private set; } = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool isNewInstance;
            _mutex = new Mutex(true, "OmeAudioAppMutex", out isNewInstance);

            if (isNewInstance)
            {
                // Start the named pipe server for communication

                if (e.Args.Contains("--no-focus") || e.Args.Contains("-nf"))
                {
                    this.NoFocus = true;
                }

                Debug.WriteLine($"No running instance detected");
                StartNamedPipeServer();
                base.OnStartup(e); // Proceed with normal startup
            }
            else
            {
                // Another instance is already running; pass the arguments to it and exit
                Debug.WriteLine($"Running instance detected");
                SendCommandLineArgsToRunningInstance(e.Args);
                Shutdown(); // Exit this new instance
            }
        }

        /// <summary>
        /// Sends command-line arguments to a running instance of the application using a named pipe.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        private void SendCommandLineArgsToRunningInstance(string[] args)
        {
            Debug.WriteLine($"Sending command-line arguments to running instance");
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(2000); // Wait up to 2 seconds to connect

                    using (var writer = new StreamWriter(client))
                    {
                        writer.AutoFlush = true;
                        // Join arguments with quotes around those that contain spaces
                        var formattedArgs = string.Join(" ", args.Select(arg => arg.Contains(" ") ? $"\"{arg}\"" : arg));
                        writer.WriteLine(formattedArgs);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to send command-line arguments to running instance: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the named pipe server to receive command-line arguments from subsequent instances.
        /// </summary>
        private void StartNamedPipeServer()
        {
            Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In))
                        {
                            server.WaitForConnection(); // Wait for a client to connect

                            using (var reader = new StreamReader(server))
                            {
                                var args = reader.ReadLine();
                                Debug.WriteLine($"Passing args to existing instance: {args}");

                                if (!string.IsNullOrEmpty(args))
                                {
                                    string executablePath = Assembly.GetExecutingAssembly().Location;
                                    // Prepend the executable path to the args string
                                    string[] parsedArgs = ParseArguments(args);
                                    parsedArgs = (new string[] { executablePath }).Concat(parsedArgs).ToArray();
                                    Application.Current.Dispatcher.Invoke(() => ProcessCommandLineArgs(parsedArgs));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error in named pipe server: {ex.Message}");
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Parses arguments, handling quoted strings to support spaces in arguments.
        /// </summary>
        /// <param name="input">The raw input string.</param>
        /// <returns>An array of parsed arguments.</returns>
        private string[] ParseArguments(string input)
        {
            var args = new List<string>();
            var currentArg = new System.Text.StringBuilder();
            bool insideQuotes = false;

            foreach (var c in input)
            {
                if (c == '\"')
                {
                    insideQuotes = !insideQuotes; // Toggle inside/outside of quotes
                }
                else if (c == ' ' && !insideQuotes)
                {
                    // If it's a space and we're not inside quotes, it's a separator
                    if (currentArg.Length > 0)
                    {
                        args.Add(currentArg.ToString());
                        currentArg.Clear();
                    }
                }
                else
                {
                    // Append characters normally
                    currentArg.Append(c);
                }
            }

            if (currentArg.Length > 0)
            {
                args.Add(currentArg.ToString());
            }

            return args.ToArray();
        }

        /// <summary>
        /// Processes command-line arguments sent to the running instance.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        private void ProcessCommandLineArgs(string[] args)
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                // Check for the no-focus parameter
                if (args.Contains("--no-focus") || args.Contains("-nf"))
                {
                    mainWindow.ShowActivated = false;
                }

                mainWindow.HandleCommandLineArgs(args); // Pass the arguments to the main window
            }
        }
    }
}
