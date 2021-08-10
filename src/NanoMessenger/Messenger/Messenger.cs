using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NanoMessenger
{
    public class Messenger : IDisposable
    {
        public static Messenger Transmitter(string nickname, string remoteHost, ushort port)
        {
            return new Messenger(nickname, remoteHost, port);
        }

        public static Messenger Receiver(string nickname, ushort port)
        {
            return new Messenger(nickname, port);
        }

        public const string INTERNAL_MESSAGE_TOKEN = "$$";
        public const string PING_MESSAGE = INTERNAL_MESSAGE_TOKEN + "PING";
        public const string PING_BACK_MESSAGE = INTERNAL_MESSAGE_TOKEN + "PINGBACK";
        public const string ACK_MESSAGE = INTERNAL_MESSAGE_TOKEN + "ACK ";
        public const string END_OF_MESSAGE = INTERNAL_MESSAGE_TOKEN + "ENDS";
        public const string CLOSE_MESSAGE = INTERNAL_MESSAGE_TOKEN + "CLOSE";
        
        public const string PIPE_ESCAPE = INTERNAL_MESSAGE_TOKEN + "PIPE";

        public const int BUFFER_SIZE = 65536;
        public const int DEFAULT_CONNECTION_TIMEOUT_IN_MILLISECONDS = 3000;
        public const int DEFAULT_PING_INTERVAL_IN_SECONDS = 3; 
        public const int DEFAULT_PING_TIMEOUT_IN_SECONDS = 5; 
        public const int DEFAULT_MAX_PING_FAILS_ALLOWED = 1; 
        public const int DEFAULT_MAX_RETRIES = -1; // <=0 == infinite retries
        public const int DEFAULT_LISTEN_TIMEOUT_IN_SECONDS = -1; // <=0 == no timeout
        public const int DEFAULT_WAIT_AFTER_DISCONNECT_IN_SECONDS = 3;
        public const int DEFAULT_RETRY_INTERVAL_IN_SECONDS = 1;

        private Thread _processMessagesTask;
        private Thread _pingTask;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;

        private bool _disposed;
        private bool _connecting;
        private bool _connected;
        private bool _disconnecting;
        private bool _listening;
        private bool _terminateThreads;
        private bool _canPing;
        private bool _pingBackPending;
        private bool _pingBackReceived;
        private bool _paused;

        private int _connectionRetriesSoFar;
        private int _pingFails;
        private DateTime _startedListening;

        private byte[] _buffer = new byte[BUFFER_SIZE];
        private string _partialMessageData = "";

        private ConcurrentQueue<QueueEntry> _messageQueue = new ConcurrentQueue<QueueEntry>();
        private object _queueLock = new object();

        public int ConnectionTimeoutInMilliseconds { get; set; } = DEFAULT_CONNECTION_TIMEOUT_IN_MILLISECONDS;
        public int PingTimeoutInSeconds { get; set; } = DEFAULT_PING_INTERVAL_IN_SECONDS;
        public int PingIntervalInSeconds { get; set; } = DEFAULT_PING_INTERVAL_IN_SECONDS;
        public int MaxAllowedFailedPings { get; set; } = DEFAULT_MAX_PING_FAILS_ALLOWED;
        public int ListenTimeoutInSeconds { get; set; } = DEFAULT_LISTEN_TIMEOUT_IN_SECONDS;
        public int MaxConnectionRetries { get; set; } = DEFAULT_MAX_RETRIES;
        public int WaitAfterDisconnectInSeconds { get; set; } = DEFAULT_WAIT_AFTER_DISCONNECT_IN_SECONDS;
        public int RetryInterval { get; set; } = DEFAULT_RETRY_INTERVAL_IN_SECONDS;

        public string Name { get; private set; }
        public IPAddress RemoteAddress { get; private set; }
        public string RemoteHostName { get; private set; }
        public string LocalHostName { get; private set; }
        public IPAddress LocalAddress { get; private set; }
        public ushort Port { get; private set; }
        public MessengerType Type { get; private set; }
        public bool Connected => _connected;
        public bool Closed => !_connected && _paused && _listener == null && _client == null && _stream == null;
        public bool PingEnabled { get; set; } = false;
        public int QueueLength => _messageQueue?.Count ?? -1;

        public event EventHandler<Message> OnReceiveMessage;
        public event EventHandler<Guid> OnReceiveAcknowledge;
        public event EventHandler OnConnecting;
        public event EventHandler OnConnectionRetry;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler OnClosing;
        public event EventHandler OnClosed;
        public event EventHandler OnMessageLoopIOException;
        public event EventHandler OnPing;
        public event EventHandler OnPingBack;
        public event EventHandler OnListenerTimedOut;
        public event EventHandler OnConnectionRetriesExceeded;

        public void BeginConnect()
        {
            if (!_connected)
            {
                if (Type == MessengerType.Receive)
                {
                    _startedListening = DateTime.Now;
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

                _paused = false;
            }
        }

        // don't forget that this does not stop the threads, so when you're done with 
        // the Messenger instance you must call Dispose() (or use the using() pattern)
        public void Close()
        {
            Close(true);
        }

        public Message QueueMessage(string text, Action<string> callbackAfterSent = null)
        {
            lock (_queueLock)
            {
                Message message = new Message(text);
                QueueEntry item = new QueueEntry(message, callbackAfterSent);
                _messageQueue.Enqueue(item);
                return message;
            }
        }

        public IEnumerable<Message> GetQueuedMessages()
        {
            lock (_queueLock)
            {
                return new List<Message>(_messageQueue.Select(x => x.Message));
            }
        }

        public Message PeekQueue()
        {
            lock (_queueLock)
            {
                return _messageQueue.TryPeek(out QueueEntry entry) ? entry.Message : null;
            }
        }

        public void ClearQueue()
        {
            lock(_queueLock)
            {
                _messageQueue = new ConcurrentQueue<QueueEntry>();
            }
        }

        public void Dispose()
        {
            _terminateThreads = true;
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
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

        private void Close(bool closedFromThisSide)
        {
            if (_connected)
            {
                if (closedFromThisSide)
                {
                    Send(CLOSE_MESSAGE);
                    Thread.Sleep(500);
                }

                _disconnecting = true;
                _paused = true;
                _canPing = false;
                _connected = false;

                OnClosing?.Invoke(this, EventArgs.Empty);

                _listener?.Stop();
                _listening = false;
                _listener = null;

                _stream?.Close();
                _client?.Close();
                _client = null;
                _stream = null;

                OnClosed?.Invoke(this, EventArgs.Empty);

                _disconnecting = false;
            }
        }

        private void PingLoop()
        {
            while (!_terminateThreads)
            {
                if (!_paused && PingEnabled && RemoteAddress != null && _connected && _canPing)
                {
                    if (!_pingBackPending)
                    {
                        OnPing?.Invoke(this, EventArgs.Empty);
                        _pingBackPending = true;
                        _pingBackReceived = false;
                        Send(PING_MESSAGE);

                        DateTime start = DateTime.Now;
                        Wait(start, PingTimeoutInSeconds, _pingBackReceived);

                        if (!_pingBackReceived)
                        {
                            _pingFails++;
                            if (_pingFails > MaxAllowedFailedPings)
                            {
                                Close();
                                OnDisconnected?.Invoke(this, EventArgs.Empty);
                                Thread.Sleep(WaitAfterDisconnectInSeconds * 1000);
                                _pingFails = 0;
                                BeginConnect();
                            }
                        }

                        Wait(start, PingIntervalInSeconds);
                        _pingBackPending = false;
                    }
                }
            }
        }

        private void Wait(DateTime waitStarted, int timeoutInSeconds, bool semaphore = false)
        {
            while (!semaphore && DateTime.Now.Subtract(waitStarted).Seconds < timeoutInSeconds) Thread.Sleep(1);
        }

        private void MessageLoop()
        {
            while (!_terminateThreads)
            {
                if (!_paused)
                {
                    if (_client == null)
                    {
                        ConnectIfReceiver();
                        ConnectIfTransmitter();
                    }
                    else
                    {
                        try
                        {
                            ReceiveIncomingMessages();
                        }
                        catch (IOException)
                        {
                            // probably the client closed down... let's start trying to reconnect
                            Close();
                            OnMessageLoopIOException?.Invoke(this, EventArgs.Empty);
                            Thread.Sleep(WaitAfterDisconnectInSeconds * 1000);
                            BeginConnect();
                        }
                        catch (Exception ex)
                        {
                            throw;
                        }

                        SendOutgoingMessage();
                    }

                    Thread.Sleep(1);
                }
            }
        }

        private void ConnectIfReceiver()
        {
            if (ListenTimeoutInSeconds > 0 && DateTime.Now.Subtract(_startedListening).TotalSeconds > ListenTimeoutInSeconds)
            {
                Close();
                OnListenerTimedOut?.Invoke(this, EventArgs.Empty);
            }
            else if (!_disconnecting && Type == MessengerType.Receive && _listener != null && _listening && _listener.Pending())
            {
                OnConnecting?.Invoke(this, EventArgs.Empty);
                try
                {
                    _client = _listener.AcceptTcpClient();
                }
                catch
                {
                    Close();
                    return;
                }

                try
                {
                    RemoteAddress = ((IPEndPoint)_client.Client.RemoteEndPoint).Address;
                    RemoteHostName = Dns.GetHostEntry(RemoteAddress).HostName;
                    if (string.IsNullOrWhiteSpace(RemoteHostName)) RemoteHostName = RemoteAddress.ToString();
                }
                catch
                {
                    // these values are for reference only... if DNS breaks, not a big deal
                    RemoteHostName = RemoteAddress.ToString();
                }

                _listener.Stop();
                _listening = false;
                _listener = null;
                _connected = true;

                GetStream();
                _canPing = true;
               
                OnConnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ConnectIfTransmitter()
        {
            if (!_disconnecting && Type == MessengerType.Transmit)
            {
                if (!_connecting)
                {
                    OnConnecting?.Invoke(this, EventArgs.Empty);
                    _connecting = true;
                }
                
                TcpClient client = new TcpClient();
                try
                {
                    // this is a simple way of enforcing a shorter connect timeout (if required)
                    client.ConnectAsync(RemoteAddress, Port, ConnectionTimeoutInMilliseconds).Wait();
                }
                catch
                {
                    // couldn't connect yet... no problem, next time round we'll try again
                }

                if (!_disconnecting && client.Connected)
                {
                    _client = client;
                    _connected = true;
                    _connecting = false;

                    GetStream();
                    _canPing = true;

                    OnConnected?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // it's possible that the connection was timed out (the timeout is something I am forcing as TcpClient doesn't have
                    // a connection timeout capability) but still completed after the timeout, leaving the
                    // client connected, so we close it here which will terminate any connection so we can retry properly,
                    // otherwise things can get out of sync
                    client.Close();
                    _connectionRetriesSoFar++;
                    if (MaxConnectionRetries > 0 && _connectionRetriesSoFar > MaxConnectionRetries)
                    {
                        _connecting = false;
                        Close();
                        OnConnectionRetriesExceeded?.Invoke(this, EventArgs.Empty);
                        _connectionRetriesSoFar = 0;
                    }
                    else
                    {
                        Thread.Sleep(RetryInterval * 1000);
                        OnConnectionRetry?.Invoke(this, EventArgs.Empty);
                    }
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

        private void SendOutgoingMessage()
        {
            if (_stream != null && _client.Connected)
            {
                lock (_queueLock)
                {
                    if (!_disconnecting && _messageQueue.Count > 0)
                    {
                        if (_messageQueue.TryDequeue(out QueueEntry topOfQueue))
                        {
                            bool sent = Send($"{ topOfQueue.Message.ToWireFormat() }");
                            if (sent)
                            {
                                topOfQueue.Callback?.Invoke(topOfQueue.Message.Text);
                            }
                        }
                    }
                }
            }
        }

        private void ReceiveIncomingMessages()
        {
            if (!_disconnecting && _stream != null && _stream.DataAvailable)
            {
                int i;
                while ((i = _stream.Read(_buffer, 0, _buffer.Length)) != 0)
                {
                    string data = $"{ _partialMessageData }{ Encoding.ASCII.GetString(_buffer, 0, i) }";
                    Array.Clear(_buffer, 0, _buffer.Length);

                    // max data in the read buffer is BUFFER_SIZE bytes which should be enough for most messages
                    // but in case it isn't, messages will be chunked; each message-end is noted with an escape sequence
                    // and if the data just read from the stream doesn't end with the end-of-message token, then
                    // we'll stuff the data into a string field and concatenate the next set of data from the stream until
                    // we have a complete set of messages available to process

                    if (data.EndsWith(END_OF_MESSAGE))
                    {
                        string[] messages = data.Split(new string[] { END_OF_MESSAGE }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (string message in messages)
                        {
                            if (message == CLOSE_MESSAGE)
                            {
                                Close(false);
                                OnDisconnected.Invoke(this, EventArgs.Empty);
                                Thread.Sleep(WaitAfterDisconnectInSeconds * 1000);
                                _pingFails = 0;
                                BeginConnect();
                            }
                            else if (message == PING_MESSAGE)
                            {
                                Send($"{ PING_BACK_MESSAGE }");
                            }
                            else if (message == PING_BACK_MESSAGE)
                            {
                                _pingBackReceived = true; // this is the semaphore for the PingLoop thread
                                OnPingBack?.Invoke(this, EventArgs.Empty);
                            }
                            else
                            {
                                if (message.StartsWith(ACK_MESSAGE))
                                {
                                    OnReceiveAcknowledge?.Invoke(this, Guid.Parse(message.Substring(ACK_MESSAGE.Length)));
                                }
                                else
                                {
                                    Message incomingMessage = Message.FromWireFormat(message);
                                    Send($"{ ACK_MESSAGE }{ incomingMessage.ID }");
                                    OnReceiveMessage?.Invoke(this, incomingMessage);
                                }
                            }
                        }

                        _partialMessageData = "";
                        break;
                    }
                    else
                    {
                        _partialMessageData = data;
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
                    byte[] textBytes = Encoding.ASCII.GetBytes($"{ text }{ END_OF_MESSAGE }");
                    _stream.Write(textBytes, 0, textBytes.Length);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        // this is only here as a fail-safe for those who don't remember to call Dispose()
        ~Messenger()
        {
            Dispose(false);
        }

        private Messenger(string name, ushort port) : this(name, null, port)
        {
            Type = MessengerType.Receive;
        }

        private Messenger(string name, string remoteHost, ushort port)
        {
            Name = name;

            LocalHostName = Dns.GetHostName();
            LocalAddress = Dns.GetHostAddresses(LocalHostName)[0];
            RemoteHostName = remoteHost;
            RemoteAddress = remoteHost == null ? null : Dns.GetHostAddresses(remoteHost)[0];
            Port = port;
            Type = MessengerType.Transmit;
            PingEnabled = true;
        }
    }
} 
