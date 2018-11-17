﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace SingleInstanceHelper
{
    public static class ApplicationActivator
    {
        public static string UniqueName { get; set; } = GetRunningProcessHash();

        private static Mutex _mutexApplication;
        private static readonly object _namedPipeServerThreadLock = new object();
        private static bool _firstApplicationInstance;
        private static NamedPipeServerStream _namedPipeServerStream;
        private static NamedPipeXmlPayload _namedPipeXmlPayload;
        private static SynchronizationContext _syncContext;
        private static Action<string[]> _otherInstanceCallback;

        private static string MutexName => $@"Mutex_{Environment.UserDomainName}_{Environment.UserName}_{UniqueName}";
        private static string PipeName => $@"Pipe_{Environment.UserDomainName}_{Environment.UserName}_{UniqueName}";

        public static bool LaunchOrReturn(Action<string[]> otherInstanceCallback, string[] args)
        {
            _otherInstanceCallback = otherInstanceCallback ?? throw new ArgumentNullException(nameof(otherInstanceCallback));

            if (IsApplicationFirstInstance())
            {
                _syncContext = SynchronizationContext.Current;
                // Setup Named Pipe listener
                NamedPipeServerCreateServer();
                return true;
            }
            else
            {
                // We are not the first instance, send the named pipe message with our payload and stop loading
                var namedPipeXmlPayload = new NamedPipeXmlPayload
                {
                    CommandLineArguments = Environment.GetCommandLineArgs().ToList()
                };

                // Send the message
                NamedPipeClientSendOptions(namedPipeXmlPayload);
                return false; // Signal to quit
            }
        }

        /// <summary>
        ///     Checks if this is the first instance of this application. Can be run multiple times.
        /// </summary>
        /// <returns></returns>
        private static bool IsApplicationFirstInstance()
        {
            // Allow for multiple runs but only try and get the mutex once
            if (_mutexApplication == null)
            {
                _mutexApplication = new Mutex(true, MutexName, out _firstApplicationInstance);
            }

            return _firstApplicationInstance;
        }

        /// <summary>
        ///     Uses a named pipe to send the currently parsed options to an already running instance.
        /// </summary>
        /// <param name="namedPipePayload"></param>
        private static void NamedPipeClientSendOptions(NamedPipeXmlPayload namedPipePayload)
        {
            try
            {
                using (var namedPipeClientStream = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    namedPipeClientStream.Connect(3000); // Maximum wait 3 seconds

                    var xmlSerializer = new XmlSerializer(typeof(NamedPipeXmlPayload));
                    xmlSerializer.Serialize(namedPipeClientStream, namedPipePayload);
                }
            }
            catch (Exception)
            {
                // Error connecting or sending
            }
        }

        /// <summary>
        ///     Starts a new pipe server if one isn't already active.
        /// </summary>
        private static void NamedPipeServerCreateServer()
        {

            // 
            // Create pipe and start the async connection wait
            _namedPipeServerStream = new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
 

            
            // Begin async wait for connections
            _namedPipeServerStream.BeginWaitForConnection(NamedPipeServerConnectionCallback, _namedPipeServerStream);
            
        }

        /// <summary>
        ///     The function called when a client connects to the named pipe. Note: This method is called on a non-UI thread.
        /// </summary>
        /// <param name="iAsyncResult"></param>
        private static void NamedPipeServerConnectionCallback(IAsyncResult iAsyncResult)
        {
            try
            {
                // End waiting for the connection
                _namedPipeServerStream.EndWaitForConnection(iAsyncResult);

                // Read data and prevent access to _namedPipeXmlPayload during threaded operations
                lock (_namedPipeServerThreadLock)
                {
                    // Read data from client
                    var xmlSerializer = new XmlSerializer(typeof(NamedPipeXmlPayload));
                    _namedPipeXmlPayload = (NamedPipeXmlPayload)xmlSerializer.Deserialize(_namedPipeServerStream);

                    // _namedPipeXmlPayload contains the data sent from the other instance
                    if (_syncContext != null)
                    {
                        _syncContext.Post(_ => _otherInstanceCallback(_namedPipeXmlPayload.CommandLineArguments.ToArray()), null);
                    }
                    else
                    {
                        _otherInstanceCallback(_namedPipeXmlPayload.CommandLineArguments.ToArray());
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // EndWaitForConnection will exception when someone calls closes the pipe before connection made
                // In that case we dont create any more pipes and just return
                // This will happen when app is closing and our pipe is closed/disposed
                return;
            }
            catch (Exception)
            {
                // ignored
            }
            finally
            {
                // Close the original pipe (we will create a new one each time)
                _namedPipeServerStream.Dispose();
            }

            // Create a new pipe for next connection
            NamedPipeServerCreateServer();
        }

        private static string GetRunningProcessHash()
        {
            using (var hash = SHA256.Create())
            {
                var processPath = Process.GetCurrentProcess().MainModule.FileName;
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(processPath));
                return Convert.ToBase64String(bytes);
            }
        }
    }
}
