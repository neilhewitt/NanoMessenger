using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NanoMessenger
{
    public class Messenger : IDisposable
    {
        public static Messenger Transmitter(string nickname, string remoteHost, ushort port, int pingTimeoutInSeconds = 5)
        {
            return new Messenger(nickname, remoteHost, port, pingTimeoutInSeconds);
        }

        public static Messenger Receiver(string nickname, ushort port, int pingTimeoutInSeconds = 5)
        {
            return new Messenger(nickname, port, pingTimeoutInSeconds);
        }

        public const string INTERNAL_MESSAGE_PREFIX = "$$";
        public const string PING_MESSAGE = INTERNAL_MESSAGE_PREFIX + "PING";
        public const string PING_BACK_MESSAGE = INTERNAL_MESSAGE_PREFIX + "PINGBACK";
        public const string ACK_MESSAGE = INTERNAL_MESSAGE_PREFIX + "ACK ";
        public const string END_OF_MESSAGE = INTERNAL_MESSAGE_PREFIX + "ENDS";

        private Thread _processMessagesTask;
        private Thread _pingTask;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;

        private bool _disposed;
        private bool _connected;
        private bool _disconnecting;
        private bool _listening;
        private bool _terminateThreads;
        private bool _pingBackPending;
        private bool _pingBackReceived;

        private int _pingTimeoutInSeconds;

        private byte[] _buffer = new byte[32768];
        private string _data = String.Empty;

        private List<QueueEntry> _messageQueue = new List<QueueEntry>();

        public string Name { get; private set; }
        public IPAddress RemoteAddress { get; private set; }
        public string RemoteHostName { get; private set; }
        public string LocalHostName { get; private set; }
        public IPAddress LocalAddress { get; private set; }
        public ushort Port { get; private set; }
        public MessengerType Type { get; private set; }
        public bool Connected => _connected;
        public bool PingEnabled { get; set; } = false;

        public event EventHandler<Message> OnReceiveMessage;
        public event EventHandler<Guid> OnReceiveAcknowledge;
        public event EventHandler OnConnecting;
        public event EventHandler<string> OnConnectionRetry;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnPing;
        public event EventHandler<string> OnPingBack;

        public void Open()
        {
            if (!_connected)
            {
                if (Type == MessengerType.Receive)
                {
                    _listener = new TcpListener(IPAddress.Any, Port);
                    _listener.Start();
                    _listening = true;
                }

                if ((_processMessagesTask == null && _pingTask == null))
                {
                    _processMessagesTask = new Thread(MessageLoop);
                    _pingTask = new Thread(PingLoop);

                    _terminateThreads = false;

                    _processMessagesTask.Start();
                    _pingTask.Start();
                }
            }
        }

        public void Close()
        {
            _terminateThreads = true;
            _processMessagesTask = null;
            _pingTask = null;
            _disconnecting = true;
            _stream?.Close();
            _client?.Close();
            _client = null;
            _stream = null;
            _listener?.Stop();
            _listener = null;
            _disconnecting = false;
            _connected = false;
        }

        public void QueueMessage(string text, Action<string> callbackAfterSent = null)
        {
            lock (_messageQueue)
            {
                if (text.StartsWith(INTERNAL_MESSAGE_PREFIX))
                {
                    throw new ArgumentException($"Message begins with illegal sequence '{ INTERNAL_MESSAGE_PREFIX }' - use '\\{ INTERNAL_MESSAGE_PREFIX }' to escape.");
                }

                QueueEntry item = new QueueEntry(new Message(text), callbackAfterSent);
                _messageQueue.Add(item);
            }
        }

        public IEnumerable<Message> GetQueuedMessages()
        {
            lock (_messageQueue)
            {
                return new List<Message>(_messageQueue.Select(x => x.Message));
            }
        }

        public Message PeekQueue()
        {
            lock (_messageQueue)
            {
                return _messageQueue[0].Message;
            }
        }

        public void ClearQueue()
        {
            lock(_messageQueue)
            {
                _messageQueue.Clear();
            }
        }

        private void PingLoop()
        {
            while (!_terminateThreads)
            {
                if (PingEnabled && RemoteAddress != null && _connected)
                {
                    if (!_pingBackPending)
                    {
                        OnPing?.Invoke(this, RemoteHostName);
                        _pingBackPending = true;
                        Send(PING_MESSAGE); // PING should be responded to with PINGBACK or else the connection is down
                        Task.Run(() => EnsurePingBack()); // we spin the wait method off onto a thread to avoid blocking
                    }
                }

                // waits 3 seconds between pings but in 6 segments of 500ms
                // so that if we close down, this thread doesn't hold up app shutdown
                // more than 500ms
                int times = 0;
                while (times++ < 6)
                {
                    Thread.Sleep(500);
                    if (_terminateThreads)
                    {
                        return;
                    }
                }
            }
        }

        private void EnsurePingBack()
        {
            DateTime start = DateTime.Now;
            
            // there must be a more attractive way of doing this... wait until the pingback happens or timeout occurs
            while (!_pingBackReceived && DateTime.Now.Subtract(start).Seconds < _pingTimeoutInSeconds) ;
            
            if (!_pingBackReceived)
            {
                OnDisconnected?.Invoke(this, EventArgs.Empty);
                Close();
                Open();
            }

            _pingBackPending = false;
        }

        private void MessageLoop()
        {
            while (!_terminateThreads)
            {
                if (_client == null)
                {
                    ConnectIfReceiver();
                    ConnectIfTransmitter();
                }
                else
                {
                    // messages are pulled from a queue, but the queue is really only there to provide resilience from disconnection;
                    // all pending messages are sent now
                    SendMessages();

                    try
                    {
                        HandleIncomingMessages();
                    }
                    catch (IOException)
                    {
                        // probably the client closed down... let's start trying to reconnect
                        Close();
                        Open();
                    }
                    catch
                    {
                        throw; // anything else is fatal
                    }
                }
            }
        }

        private void ConnectIfReceiver()
        {
            if (Type == MessengerType.Receive && _listener != null && _listening && _listener.Pending())
            {
                OnConnecting?.Invoke(this, EventArgs.Empty);
                _client = _listener.AcceptTcpClient();
                RemoteAddress = ((IPEndPoint)_client.Client.RemoteEndPoint).Address;
                RemoteHostName = Dns.GetHostEntry(RemoteAddress).HostName;
                _listener.Stop();
                _listening = false;
                _connected = true;
                OnConnected?.Invoke(this, EventArgs.Empty);

                GetStream();
            }
        }

        private void ConnectIfTransmitter()
        {
            if (Type == MessengerType.Transmit)
            {
                OnConnecting?.Invoke(this, EventArgs.Empty);
                TcpClient client = new TcpClient();
                try
                {
                    client.Connect(RemoteAddress, Port);
                }
                catch (Exception ex)
                {
                    // couldn't connect yet... no problem, next time round we'll try again
                }

                if (client.Connected)
                {
                    _client = client;
                    _connected = true;
                    OnConnected?.Invoke(this, EventArgs.Empty);

                    GetStream();
                }
                else
                {
                    OnConnectionRetry?.Invoke(this, RemoteHostName);
                    Thread.Sleep(1000);
                }
            }
        }

        private void GetStream()
        {
            if (_stream == null && _client != null && _client.Connected)
            {
                _stream = _client.GetStream();
            }
        }

        private void SendMessages()
        {
            if (_stream != null && _client.Connected)
            {

                lock (_messageQueue)
                {
                    if (!_disconnecting && _messageQueue.Count > 0)
                    {
                        List<QueueEntry> sentMessages = new List<QueueEntry>();
                        foreach (QueueEntry messageAndCallback in _messageQueue)
                        {
                            if (Send($"{ messageAndCallback.Message.ToString() }"))
                            {
                                sentMessages.Add(messageAndCallback);
                            }

                            messageAndCallback.Callback?.Invoke($"{ messageAndCallback.Message.ToString() }");
                        }

                        foreach (QueueEntry entry in sentMessages)
                        {
                            _messageQueue.Remove(entry);
                        }
                    }
                }
            }
        }

        private void HandleIncomingMessages()
        {
            if (!_disconnecting && _stream != null && _stream.DataAvailable)
            {
                int i;
                while ((i = _stream.Read(_buffer, 0, _buffer.Length)) != 0)
                {
                    string message = $"{ _data }{ System.Text.Encoding.ASCII.GetString(_buffer, 0, i) }";

                    // max data in the read buffer is 32768 bytes which should be enough
                    // but in case it isn't... messages will be chunked; message-end is noted with an escape sequence

                    if (message.EndsWith(END_OF_MESSAGE))
                    {
                        string cleanedMessage = message.Replace(END_OF_MESSAGE, String.Empty);
                        if (cleanedMessage != String.Empty)
                        {
                            if (cleanedMessage == PING_MESSAGE)
                            {
                                // respond to the incoming ping with a pingback
                                Send($"{ PING_BACK_MESSAGE }");
                            }
                            else if (cleanedMessage == PING_BACK_MESSAGE)
                            {
                                _pingBackReceived = true; // this is a semaphore for the EnsurePingBack method above
                                OnPingBack?.Invoke(this, RemoteHostName);
                            }
                            else
                            {
                                if (cleanedMessage.StartsWith(ACK_MESSAGE))
                                {
                                    // clients can subscribe to OnReceiveAcknowledge to know when their messages
                                    // got there - this is the limit of any auditing we do here
                                    OnReceiveAcknowledge?.Invoke(this, Guid.Parse(cleanedMessage.Substring(ACK_MESSAGE.Length)));
                                }
                                else
                                {
                                    Message incomingMessage = Message.Parse(cleanedMessage);

                                    // all messages received are acknowledged back to the client
                                    // in case the client needs to know when it's been delivered
                                    Send($"{ ACK_MESSAGE }{ incomingMessage.ID }");

                                    OnReceiveMessage?.Invoke(this, incomingMessage);
                                }
                            }
                        }

                        // buffer must be cleared to avoid corruption if the next message is shorter than the last one
                        Array.Clear(_buffer, 0, _buffer.Length);
                        _data = String.Empty;
                        break;
                    }
                    else
                    {
                        // store the current partial message for next time around the loop
                        _data = message;
                    }
                }
            }
        }

        private bool Send(string text)
        {
            if (_stream != null && _client != null && _connected && _client.Connected)
            {
                try
                {
                    byte[] textBytes = Encoding.ASCII.GetBytes($"{text}{ END_OF_MESSAGE }");
                    _stream.Write(textBytes, 0, textBytes.Length);
                    Thread.Sleep(25); 
                    return true;
                }
                catch
                {
                    // connection was forcibly closed - shut it down and wait for re-connection
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
                    Close();
                    Open();
                    return false;
                }
            }

            return false;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Close();
                }

                _disposed = true;
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private Messenger(string name, ushort port, int pingTimeOutInSeconds = 5) : this(name, null, port, pingTimeOutInSeconds)
        {
            Type = MessengerType.Receive;
        }

        private Messenger(string name, string remoteHost, ushort port, int pingTimeOutInSeconds = 5)
        {
            Name = name;

            LocalHostName = Dns.GetHostName();
            LocalAddress = Dns.GetHostAddresses(LocalHostName)[0];
            RemoteHostName = remoteHost;
            RemoteAddress = remoteHost == null ? null : Dns.GetHostAddresses(remoteHost)[0];
            Port = port;
            Type = MessengerType.Transmit;
            _pingTimeoutInSeconds = pingTimeOutInSeconds;
        }
    }
} 
