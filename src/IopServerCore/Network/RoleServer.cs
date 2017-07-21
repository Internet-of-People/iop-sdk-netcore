using IopCommon;
using IopServerCore.Kernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace IopServerCore.Network
{
  /// <summary>
  /// Implementation of an asynchronous TCP server with optional TLS encryption
  /// that provides services for one or more server roles.
  /// </summary>
  public class TcpRoleServer<TIncomingClient, TMessage> where TIncomingClient : IncomingClientBase<TMessage>
  {
    /// <summary>Instance logger.</summary>
    private Logger log;

    /// <summary>Shutdown signaling object.</summary>
    public ComponentShutdown ShutdownSignaling;

    /// <summary>.NET representation of TCP server.</summary>
    public TcpListener Listener;

    /// <summary>true if the server has TLS encryption enabled.</summary>
    public bool UseTls;

    /// <summary>Specification of network interface and port that the TCP server listens on.</summary>
    public IPEndPoint EndPoint;

    /// <summary>One or more roles of the server.</summary>
    public uint Roles;

    /// <summary>true if the TCP server is running, false otherwise.</summary>
    public bool IsRunning = false;


    /// <summary>Internal server ID that is formed of server roles. It is used as the base of message numbering.</summary>
    public uint IdBase;

    /// <summary>Event that is set when acceptThread is not running.</summary>
    private ManualResetEvent acceptThreadFinished = new ManualResetEvent(true);

    /// <summary>Thread that is waiting for the new clients to connect to the TCP server port.</summary>
    private Thread acceptThread;


    /// <summary>Queue of clients, which is produced by acceptThread and consumed by clientQueueHandlerThread.</summary>
    private Queue<TcpClient> clientQueue = new Queue<TcpClient>();
    
    /// <summary>Synchronization object for exclusive access to clientQueue.</summary>
    private object clientQueueLock = new object();

    /// <summary>Event that is set when a new client has been added to clientQueue.</summary>
    private AutoResetEvent clientQueueEvent = new AutoResetEvent(false);


    /// <summary>Event that is set when clientQueueHandlerThread is not running.</summary>
    private ManualResetEvent clientQueueHandlerThreadFinished = new ManualResetEvent(true);

    /// <summary>Thread that is responsible for handling new clients that were accepted by acceptThread.</summary>
    private Thread clientQueueHandlerThread;

    /// <summary>List of server's network peers and clients.</summary>
    private IncomingClientList<TMessage> clientList;

    /// <summary>Pointer to the Network.Server component.</summary>
    private ServerBase<TIncomingClient, TMessage> serverComponent;


    /// <summary>Number of milliseconds after which the server's client is considered inactive and its connection can be terminated.</summary>
    private int clientKeepAliveTimeoutMs;


    /// <summary>
    /// Creates a new TCP server to listen on specific IP address and port.
    /// </summary>
    /// <param name="Interface">IP address of the interface on which the TCP server should listen. IPAddress.Any is a valid value.</param>
    /// <param name="Port">TCP port on which the TCP server should listen.</param>
    /// <param name="UseTls">Indication of whether to use TLS for this TCP server.</param>
    /// <param name="Roles">One or more roles of this server.</param>
    /// <param name="ClientKeepAliveTimeoutMs">Number of milliseconds after which the server's client is considered inactive and its connection can be terminated.</param>
    public TcpRoleServer(IPAddress Interface, int Port, bool UseTls, uint Roles, int ClientKeepAliveTimeoutMs) :
      this(new IPEndPoint(Interface, Port), UseTls, Roles, ClientKeepAliveTimeoutMs)
    {      
    }

    /// <summary>
    /// Creates a new TCP server to listen on specific IP endpoint.
    /// </summary>
    /// <param name="EndPoint">Specification of the interface and TCP port on which the TCP server should listen. IPAddress.Any is a valid value for the interface.</param>
    /// <param name="UseTls">Indication of whether to use TLS for this TCP server.</param>
    /// <param name="Roles">One or more roles of this server.</param>
    /// <param name="ClientKeepAliveTimeoutMs">Number of milliseconds after which the server's client is considered inactive and its connection can be terminated.</param>
    public TcpRoleServer(IPEndPoint EndPoint, bool UseTls, uint Roles, int ClientKeepAliveTimeoutMs)
    {
      string logPrefix = string.Format("[{0}/tcp{1}] ", EndPoint.Port, UseTls ? "_tls" : "");
      log = new Logger("IopServerCore.Network.TcpRoleServer", logPrefix);

      log.Trace("(EndPoint:'{0}',UseTls:{1},Roles:{2},ClientKeepAliveTimeoutMs:{3})", EndPoint, UseTls, Roles, ClientKeepAliveTimeoutMs);
      this.UseTls = UseTls;
      this.Roles = Roles;
      this.EndPoint = EndPoint;
      this.clientKeepAliveTimeoutMs = ClientKeepAliveTimeoutMs;

      ShutdownSignaling = new ComponentShutdown(Base.ComponentManager.GlobalShutdown);

      serverComponent = (ServerBase<TIncomingClient, TMessage>)Base.ComponentDictionary[ServerBase<TIncomingClient, TMessage>.ComponentName];
      clientList = serverComponent.GetClientList();

      IsRunning = false;
      Listener = new TcpListener(this.EndPoint);
      Listener.Server.LingerState = new LingerOption(true, 0);
      Listener.Server.NoDelay = true;

      IdBase = ((uint)Roles << 24);
      log.Trace("(-)");
    }

    /// <summary>
    /// <para>Starts the TCP server listener and starts client thread handlers.</para>
    /// <para>If the application is restarted, it may be the case that the TCP port 
    /// is unusable for a short period of time. This method repeatedly tries to reuse that port until it succeeds 
    /// or until 10 unsuccessful attempts are reached.</para>
    /// </summary>
    /// <returns>true if the function succeeds, false otherwise</returns>
    public bool Start()
    {
      log.Info("(Roles:[{0}])", this.Roles);

      int tryCounter = 0;
      bool res = false;
      while (tryCounter < 10)
      {
        try
        {
          this.Listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
          this.Listener.Start();
          res = true;
          break;
        }
        catch (SocketException se)
        {
          log.Info("Socket error code {0} occurred while trying to reuse socket: {1}.", se.SocketErrorCode, se.ToString());
        }

        int waitTime = tryCounter * 3;
        log.Info("Will wait {0} seconds and then try again.", waitTime);
        Thread.Sleep(waitTime * 1000);
        tryCounter++;
      }

      if (res)
      {
        clientQueueHandlerThread = new Thread(new ThreadStart(ClientQueueHandlerThread));
        clientQueueHandlerThread.Start();

        acceptThread = new Thread(new ThreadStart(AcceptThread));
        acceptThread.Start();

        IsRunning = true;
      }

      log.Info("(-):{0}", res);
      return res;
    }


    /// <summary>
    /// Stops TCP server and frees resources associated with it.
    /// </summary>
    public void Stop()
    {
      log.Info("()");

      ShutdownSignaling.SignalShutdown();

      try
      {
        Listener.Stop();

        if ((clientQueueHandlerThread != null) && !clientQueueHandlerThreadFinished.WaitOne(10000))
          log.Error("Client queue handler thread did not terminated in 10 seconds.");

        if ((acceptThread != null) && !acceptThreadFinished.WaitOne(10000))
          log.Error("Accept thread did not terminated in 10 seconds.");

        lock (clientQueueLock)
        {
          log.Info("Closing {0} clients from new clients queue.", clientQueue.Count);
          while (clientQueue.Count > 0)
          {
            TcpClient client = clientQueue.Dequeue();
            NetworkStream stream = client.GetStream();
            if (stream != null) stream.Dispose();
            client.Dispose();
          }
        }
      }
      catch (Exception e)
      {
        log.Error("Exception occurred: {0}", e.ToString());
      }

      log.Info("(-)");
    }


    /// <summary>
    /// Thread procedure that is responsible for accepting new clients on the TCP server port.
    /// New clients are put into clientQueue, from which they are consumed by clientQueueHandlerThread.
    /// </summary>
    private void AcceptThread()
    {
      LogDiagnosticContext.Start();

      log.Trace("()");

      acceptThreadFinished.Reset();

      AutoResetEvent acceptTaskEvent = new AutoResetEvent(false);

      while (!ShutdownSignaling.IsShutdown)
      {
        log.Debug("Waiting for new client.");
        Task<TcpClient> acceptTask = Listener.AcceptTcpClientAsync();
        acceptTask.ContinueWith(t => acceptTaskEvent.Set());

        WaitHandle[] handles = new WaitHandle[] { acceptTaskEvent, ShutdownSignaling.ShutdownEvent };
        int index = WaitHandle.WaitAny(handles);
        if (handles[index] == ShutdownSignaling.ShutdownEvent)
        {
          log.Info("Shutdown detected.");
          break;
        }

        try
        {
          // acceptTask is finished here, asking for Result won't block.
          TcpClient client = acceptTask.Result;
          EndPoint ep = client.Client.RemoteEndPoint;
          lock (clientQueueLock)
          {
            clientQueue.Enqueue(client);
          }
          log.Debug("New client '{0}' accepted.", ep);
          clientQueueEvent.Set();
        }
        catch (Exception e)
        {
          log.Error("Exception occurred: {0}", e.ToString());
        }
      }

      acceptThreadFinished.Set();

      log.Trace("(-)");

      LogDiagnosticContext.Stop();
    }


    /// <summary>
    /// Thread procedure that consumes clients from clientQueue. 
    /// When a new client is detected in the queue, it is removed from the queue 
    /// and enters asynchronous read and processing loop.
    /// </summary>
    private void ClientQueueHandlerThread()
    {
      log.Info("()");

      clientQueueHandlerThreadFinished.Reset();

      while (!ShutdownSignaling.IsShutdown)
      {
        WaitHandle[] handles = new WaitHandle[] { clientQueueEvent, ShutdownSignaling.ShutdownEvent };
        int index = WaitHandle.WaitAny(handles);
        if (handles[index] == ShutdownSignaling.ShutdownEvent)
        {
          log.Info("Shutdown detected.");
          break;
        }

        log.Debug("New client in the queue detected, queue count is {0}.", clientQueue.Count);
        bool queueEmpty = false;
        while (!queueEmpty && !ShutdownSignaling.IsShutdown)
        {
          TcpClient tcpClient = null;
          lock (clientQueueLock)
          {
            if (clientQueue.Count > 0)
              tcpClient = clientQueue.Peek();
          }

          if (tcpClient != null)
          {
            ulong clientId = clientList.GetNewClientId();
            string logPrefix = string.Format("[{0}<=>{1}|{2}] ", EndPoint, tcpClient.Client.RemoteEndPoint, clientId.ToHex());
            TIncomingClient client = Activator.CreateInstance(typeof(TIncomingClient), new object[] { this, tcpClient, clientId, UseTls, clientKeepAliveTimeoutMs, logPrefix }) as TIncomingClient;
            ClientHandlerAsync(client);

            lock (clientQueueLock)
            {
              clientQueue.Dequeue();
              queueEmpty = clientQueue.Count == 0;
            }
          }
          else queueEmpty = true;
        }
      }

      clientQueueHandlerThreadFinished.Set();

      log.Info("(-)");
    }


    /// <summary>
    /// Handler for each client that connects to the TCP server.
    /// </summary>
    /// <param name="Client">Client that is connected to TCP server.</param>
    /// <remarks>The client is being handled in the processing loop until the connection to it is terminated by either side.</remarks>
    private async void ClientHandlerAsync(IncomingClientBase<TMessage> Client)
    {
      LogDiagnosticContext.Start();

      log.Info("(Client.RemoteEndPoint:{0})", Client.RemoteEndPoint);

      clientList.AddNetworkPeer(Client);
      log.Debug("Client ID set to {0}.", Client.Id.ToHex());

      await Client.ReceiveMessageLoop();

      // Free resources used by the client.
      clientList.RemoveNetworkPeer(Client);
      await Client.HandleDisconnect();
      Client.Dispose();

      log.Info("(-)");

      LogDiagnosticContext.Stop();
    }
  }
}
