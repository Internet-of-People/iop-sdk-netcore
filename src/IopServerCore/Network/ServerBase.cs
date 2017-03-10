using IopCommon;
using IopCrypto;
using IopServerCore.Kernel;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace IopServerCore.Network
{
  /// <summary>
  /// Network server component is responsible managing the role TCP servers.
  /// </summary>
  public abstract class ServerBase<TIncomingClient> : Component where TIncomingClient : IncomingClientBase
  {
    /// <summary>Name of the component. This must match the name of the derived component.</summary>
    public const string ComponentName = "Network.Server";

    /// <summary>Class logger.</summary>
    private static Logger log = new Logger("IopServerCore." + ComponentName);

    /// <summary>Collection of running TCP role servers sorted by their port.</summary>
    private Dictionary<int, TcpRoleServer<TIncomingClient>> tcpServers = new Dictionary<int, TcpRoleServer<TIncomingClient>>();


    /// <summary>List of network peers and clients across all role servers.</summary>
    private IncomingClientList clientList;


    /// <summary>Server's network identifier.</summary>
    private byte[] serverId;
    /// <summary>Server's network identifier.</summary>
    public byte[] ServerId { get { return serverId; } }



    /// <summary>
    /// Initializes the component.
    /// </summary>
    public ServerBase():
      base(ComponentName)
    {
    }


    public override bool Init()
    {
      log.Info("()");

      bool res = false;
      bool error = false;

      try
      {
        ConfigBase config = (ConfigBase)Base.ComponentDictionary[ConfigBase.ComponentName];
        serverId = Crypto.Sha256(config.Keys.PublicKey);
        clientList = new IncomingClientList();

        foreach (RoleServerConfiguration roleServer in config.ServerRoles.RoleServers.Values)
        {
          if (roleServer.IsTcpServer)
          {
            IPEndPoint endPoint = new IPEndPoint(config.BindToInterface, roleServer.Port);
            TcpRoleServer<TIncomingClient> server = new TcpRoleServer<TIncomingClient>(endPoint, roleServer.Encrypted, roleServer.Roles, roleServer.ClientKeepAliveTimeoutMs);
            tcpServers.Add(server.EndPoint.Port, server);
          }
          else
          {
            log.Fatal("UDP servers are not supported.");
            error = true;
            break;
          }
        }


        foreach (TcpRoleServer<TIncomingClient> server in tcpServers.Values)
        {
          if (!server.Start())
          {
            log.Error("Unable to start TCP server {0}.", server.EndPoint);
            error = true;
            break;
          }
        }

        if (!error)
        {
          res = true;
          Initialized = true;
        }
      }
      catch (Exception e)
      {
        log.Error("Exception occurred: {0}", e.ToString());
      }

      if (!res)
      {
        ShutdownSignaling.SignalShutdown();

        foreach (TcpRoleServer<TIncomingClient> server in tcpServers.Values)
        {
          if (server.IsRunning)
            server.Stop();
        }
        tcpServers.Clear();
      }

      log.Info("(-):{0}", res);
      return res;
    }


    public override void Shutdown()
    {
      log.Info("()");

      ShutdownSignaling.SignalShutdown();

      List<IncomingClientBase> clients = clientList.GetNetworkClientList();
      try
      {
        log.Info("Closing {0} existing client connections of role servers.", clients.Count);
        foreach (IncomingClientBase client in clients)
          client.CloseConnection();
      }
      catch
      {
      }

      foreach (TcpRoleServer<TIncomingClient> server in tcpServers.Values)
      {
        if (server.IsRunning)
          server.Stop();
      }
      tcpServers.Clear();

      log.Info("(-)");
    }




    /// <summary>
    /// This method is responsible for going through all existing client connections 
    /// and closing those that are inactive for more time than allowed for a particular client type.
    /// Note that we are touching resources from different threads, so we have to expect the object are being 
    /// disposed at any time.
    /// </summary>
    public async Task CheckInactiveClientConnectionsAsync()
    {
      log.Trace("()");

      try
      {
        List<IncomingClientBase> clients = clientList.GetNetworkClientList();
        foreach (IncomingClientBase client in clients)
        {
          ulong id = 0;
          try
          {
            id = client.Id;
            log.Trace("Client ID {0} has NextKeepAliveTime set to {1}.", id.ToHex(), client.NextKeepAliveTime.ToString("yyyy-MM-dd HH:mm:ss"));
            if (client.NextKeepAliveTime < DateTime.UtcNow)
            {
              // Client's connection is now considered inactive. 
              // We want to disconnect the client and remove it from the list.
              // If we dispose the client this will terminate the read loop in TcpRoleServer.ClientHandlerAsync,
              // which will then remove the client from the list, so we do not need to care about that.
              log.Debug("Client ID {0} did not send any requests before {1} and is now considered as inactive. Closing client's connection.", id.ToHex(), client.NextKeepAliveTime.ToString("yyyy-MM-dd HH:mm:ss"));
              await client.CloseConnectionAsync();
            }
          }
          catch (Exception e)
          {
            log.Info("Exception occurred while working with client ID {0}: {1}", id, e.ToString());
          }
        }
      }
      catch (Exception e)
      {
        log.Error("Exception occurred: {0}", e.ToString());
      }

      log.Trace("(-)");
    }

    /// <summary>
    /// Obtains list of running role servers.
    /// </summary>
    /// <returns>List of running role servers.</returns>
    public List<TcpRoleServer<TIncomingClient>> GetRoleServers()
    {
      log.Trace("()");

      List<TcpRoleServer<TIncomingClient>> res = new List<TcpRoleServer<TIncomingClient>>(tcpServers.Values);

      log.Trace("(-):*.Count={0}", res.Count);
      return res;
    }

    /// <summary>
    /// Obtains the client list.
    /// </summary>
    /// <returns>List of all server's clients.</returns>
    public IncomingClientList GetClientList()
    {
      log.Trace("()");

      IncomingClientList res = clientList;

      log.Trace("(-)");
      return res;
    }
  }
}
