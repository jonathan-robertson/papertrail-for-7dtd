using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PapertrailFor7DTD.SDK {
    /**
     * <summary>Fowards Unity logs to Papertrail's monitoring servers using the syslog protocol.</summary>
     */
    public class PapertrailLogger : MonoBehaviour {
        /**
         * <summary>Format for messages that use a tag</summary>
         */
        private const string s_taggedNoStack = "tag=[{0}] message=[{1}]";
        /** 
         * <summary>Format for messages that use a tag</summary>
         */
        private const string s_logFormatNoStack = "message=[{0}]";
        /** 
         * <summary>Format for messages that use a tag</summary>
         */
        private const string s_taggedLogFormat = "tag=[{0}] message=[{1}] stacktrace=[{2}]";
        /** 
         * <summary>Format for messsage without a tag</summary>
         */
        private const string s_logFormat = "message=[{0}] stacktrace=[{1}]";
        /** 
         * <summary>Additional formatting for logging the client ip address</summary>
         */
        private const string s_ipPrefixFormat = "ip=[{0}] {1}";
        /** 
         * <summary>Singleton instance of the PapertrailLogger</summary>
         */
        public static PapertrailLogger Instance {
            get {
                if (s_instance == null) {
                    Initialize();
                }
                return s_instance;
            }
        }
        /**
         * <summary>Private singleton instnace storage</summary>
         */
        private static PapertrailLogger s_instance;

        /**
         * <summary>Papertrail logging settings</summary>
         */
        public PapertrailSettings Settings { get; private set; }
        /**
         * <summary>UDP client for sending messages</summary>
         */
        private UdpClient m_udpClient = null;
        /**
         * <summary>Builds messages with minimal garbage allocations</summary>
         */
        private readonly StringBuilder m_stringBuilder = new StringBuilder();
        /**
         * <summary>Name of the running application</summary>
         */
        private string m_processName;
        /**
         * <summary>Platform the app is running on</summary>
         */
        private string m_platform;
        /**
         * <summary>The clients external IP address</summary>
         */
        private string m_localIp;
        /**
         * <summary>Flag set when the client is connected and ready to being logging</summary>
         */
        private bool m_isReady;
        /**
         * <summary>Log messages are queued up until the client is ready to log</summary>
         */
        private readonly Queue<string> m_queuedMessages = new Queue<string>();
        /**
         * <summary>User set tag for log messages</summary>
         */
        private string m_tag;

        public static bool IsEnabled { get; set; } = false;

        /**
         * <summary>Initializes the logging instance as soon as the app starts</summary>
         */
        [RuntimeInitializeOnLoadMethod]
        internal static void Initialize() {
            if (s_instance == null) {
                s_instance = FindObjectOfType<PapertrailLogger>();
                if (s_instance == null) {
                    s_instance = new GameObject("PapertrailLogger").AddComponent<PapertrailLogger>();
                }
                DontDestroyOnLoad(s_instance.gameObject);
            }
        }

        /**
         * <summary>Called when the Instance is created. Gathers application information and creates the UDP client</summary>
         */
        internal void Awake() {
            // Ensure this is the only instance
            if (s_instance != null && s_instance != this) {
                Destroy(this);
                return;
            }
            // Load settings
            m_isReady = false;
            Settings = PapertrailSettings.LoadSettings();
            if (string.IsNullOrEmpty(Settings.hostname)) {
                return;
            }
            IsEnabled = true;

            // Store app information
            m_processName = Application.identifier.Replace(" ", string.Empty);
            m_platform = Application.platform.ToString().ToLowerInvariant();
            m_localIp = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses.ToList())
                .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.Address.ToString())
                .First();

            if (!string.IsNullOrEmpty(Settings.hostname) && Settings.port > 0) {
                try {
                    // Create the udp client
                    m_udpClient = new UdpClient(Settings.hostname, Settings.port);
                    // Hook into Unity's logging system
                    Application.logMessageReceivedThreaded += Application_LogMessageReceived;
                    // Begin looking for a connection
                    StartCoroutine(GetExternalIP());
                } catch (Exception ex) {
                    m_udpClient = null;
                    Debug.LogException(ex);
                }
            } else {
                m_udpClient = null;
            }
        }

        /**
         * <summary>Called when the instance is destroyed and closes the client</summary>
         */
        private void OnDestroy() {
            // Unhook from Unity's logging system
            Application.logMessageReceivedThreaded -= Application_LogMessageReceived;
            // Close the UDP client
            Close();
        }

        /**
         * <summary>Closes the connected UDP client</summary>
         */
        private void Close() {
            // Close the UDP client
            if (m_udpClient != null) {
                m_udpClient.Close();
                m_udpClient = null;
            }
        }

        /**
         * <summary>Retrieves the external IP address of the client to append to log messages.<br />Waits until an internet connection can be established before starting logs.</summary>
         */
        private IEnumerator GetExternalIP() {
            // Wait for an internet connection
            while (Application.internetReachability == NetworkReachability.NotReachable) {
                yield return new WaitForSeconds(1);
            }
            while (true) {
                // Find the client's external IP address
                UnityWebRequest webRequest = UnityWebRequest.Get("https://api.ipify.org?format=text");
                yield return webRequest.SendWebRequest();
                if (webRequest.result != UnityWebRequest.Result.ConnectionError) {
                    m_localIp = webRequest.downloadHandler.text;
                    break;
                }
                yield return new WaitForSeconds(1);
            }
            // Set the logger as ready to send messages
            m_isReady = true;
            Debug.Log("Papertrail Logger Initialized");
            // Send all messages that were waiting for initialization
            while (m_queuedMessages.Count > 0) {
                BeginSend(m_queuedMessages.Dequeue());
                yield return null;
            }
        }

        /**
         * <summary>Callback for the Unity logging system. Happens off of the main thread</summary>
         */
        private void Application_LogMessageReceived(string condition, string stackTrace, LogType type) {
            // Set the severity type based on the Unity's log level
            Severity severity = Severity.Debug;
            switch (type) {
                case LogType.Assert:
                    severity = Severity.Alert;
                    break;
                case LogType.Error:
                case LogType.Exception:
                    severity = Severity.Error;
                    break;
                case LogType.Log:
                    severity = Severity.Debug;
                    break;
                case LogType.Warning:
                    severity = Severity.Warning;
                    break;
            }
            try {
                if (severity > Settings.minimumLoggingLevel) {
                    return;
                }

                if (!string.IsNullOrEmpty(m_tag)) {
                    // Log the message with the set tag
                    if (Settings.logStackTrace) {
                        Log(severity, string.Format(s_taggedLogFormat, m_tag, condition, stackTrace));
                    } else {
                        Log(severity, string.Format(s_taggedNoStack, m_tag, condition));
                    }
                } else {
                    // Log the message without a tag
                    if (Settings.logStackTrace) {
                        Log(severity, string.Format(s_logFormat, condition, stackTrace));
                    } else {
                        Log(severity, string.Format(s_logFormatNoStack, condition));
                    }
                }
            } catch (Exception ex) {
                Debug.LogException(ex);
            }
        }

        /**
         * <summary>Begin sending a message asynchrously on the UDP client</summary>
         */
        private void BeginSend(string msg) {
            if (string.IsNullOrEmpty(msg)) {
                return;
            }

            if (!m_isReady) {
                // Enqueue messages if the logger isn't fully initialized
                m_queuedMessages.Enqueue(msg);
                return;
            }
            if (m_udpClient != null) {
                // Get message bytes
                byte[] data = Encoding.UTF8.GetBytes(msg);
                try {
                    // Send over the udp socket
                    m_udpClient.BeginSend(data, data.Length, OnEndSend, m_udpClient);
                } catch (Exception e) {
                    Debug.LogException(e);
                }
            }
        }

        /**
         * <summary>Callback to finish sending the UDP message</summary>
         */
        private void OnEndSend(IAsyncResult result) {
            try {
                // Complete the UDP send
                UdpClient udpClient = (UdpClient)result.AsyncState;
                udpClient.EndSend(result);
            } catch (Exception e) {
                Debug.LogException(e);
            }
        }

        /**
         * <summary>Internal instance logging of a message</summary>
         */
        private void LogInternal(string msg) {
            Log(Settings.facility, Severity.Debug, msg);
        }

        /**
         * <summary>Internal instance logging of a message</summary>
         */
        private void LogInternal(Severity severity, string msg) {
            Log(Settings.facility, severity, msg);
        }

        /**
         * <summary>Internal instance logging of a message</summary>
         */
        private void LogInternal(Facility facility, Severity severity, string msg) {
            // Early out if the client's logging level is lower than the log message
            if (string.IsNullOrEmpty(msg) || severity > Settings.minimumLoggingLevel || m_udpClient == null) {
                return;
            }
            // Calculate the message severity (facility * 8 + severity)
            int severityValue = ((int)facility) * 8 + (int)severity;
            string message = string.Empty;
            // Build the syslog message format
            lock (m_stringBuilder) {
                // Reset the string builder
                m_stringBuilder.Length = 0;
                // Severity
                m_stringBuilder.Append('<');
                m_stringBuilder.Append(severityValue);
                m_stringBuilder.Append('>');
                // Version 1
                m_stringBuilder.Append('1');
                m_stringBuilder.Append(' ');
                // Date time stamp in RFC3339 format
                m_stringBuilder.Append(Rfc3339DateTime.ToString(DateTime.UtcNow));
                m_stringBuilder.Append(' ');
                // The application that is logging
                string systemName = Settings.systemName;
                if (string.IsNullOrEmpty(systemName)) {
                    systemName = "7dtd-server";
                }

                m_stringBuilder.Append(systemName);
                m_stringBuilder.Append(' ');
                // Process name that is logging
                m_stringBuilder.Append(m_processName);
                m_stringBuilder.Append(' ');
                // Platform the client is running on
                m_stringBuilder.Append(m_platform);
                m_stringBuilder.Append(' ');
                // The log message with the client IP
                if (Settings.logClientIPAddress) {
                    m_stringBuilder.Append(string.Format(s_ipPrefixFormat, m_localIp, msg));
                } else {
                    m_stringBuilder.Append(msg);
                }
                message = m_stringBuilder.ToString();
            }
            // Send the completed message
            BeginSend(message);
        }

        /**
         * <summary>Set a user tag to be appended to all outgoing logs</summary>
         * <param name="tag">Tag that will appended to all outgoing messages</param>
         */
        public static void SetTag(string tag) {
            Instance.m_tag = tag;
        }

        /**
         * <summary>Log a message to the remote server</summary>
         * <param name="msg">Message to be logged</param>
         */
        public static void Log(string msg) {
            Instance.LogInternal(Severity.Debug, msg);
        }

        /**
         * <summary>Log a message to the remote server</summary>
         * <param name="severity">Severity level of the message</param>
         * <param name="msg">Message to be logged</param>
         */
        public static void Log(Severity severity, string msg) {
            Instance.LogInternal(severity, msg);
        }

        /**
         * <summary>Log a message to the remote server</summary>
         * <param name="facility">The sending facility of the message. See syslog protocol for more information.</param>
         * <param name="severity">Severity level of the message</param>
         * <param name="msg">Message to be logged</param>
         */
        public static void Log(Facility facility, Severity severity, string msg) {
            Instance.LogInternal(facility, severity, msg);
        }
    }
}
