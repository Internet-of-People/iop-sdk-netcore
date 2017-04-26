using Iop.Locnet;
using IopCommon;
using IopCrypto;
using IopProtocol;
using IopServerCore.Kernel;
using IopServerCore.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace IopServerCore.Network.LOC
{
  public class LocClient : ClientBase
  {
    /// <summary>LOC message builder for the TCP client.</summary>
    private LocMessageBuilder messageBuilder;
    /// <summary>LOC message builder for the TCP client.</summary>
    public LocMessageBuilder MessageBuilder { get { return messageBuilder; } }

    /// <summary>Message reader connected to an open network stream.</summary>
    RawMessageReader messageReader;

    /// <summary>Owning component's shutdown signaling.</summary>
    private ComponentShutdown shutdownSignaling;

    /// <summary>Configuration component.</summary>
    private ConfigBase config;

    /// <summary>Module responsible for processing logic behind incoming messages.</summary>
    private IMessageProcessor messageProcessor;
    /// <summary>Module responsible for processing logic behind incoming messages.</summary>
    public IMessageProcessor MessageProcessor { get { return messageProcessor; } }


    /// <summary>
    /// Initialize the object.
    /// </summary>
    /// <param name="ServerEndPoint">LOC server address and port.</param>
    public LocClient(IPEndPoint ServerEndPoint, IMessageProcessor MessageProcessor, ComponentShutdown ShutdownSignaling) :
      base(ServerEndPoint, false)
    {
      log = new Logger("IopServerCore.Network.LOC.LocClient");
      log.Trace("(ServerEndPoint:'{0}')", ServerEndPoint);

      config = (ConfigBase)Base.ComponentDictionary[ConfigBase.ComponentName];

      messageBuilder = new LocMessageBuilder(0, new List<SemVer> { SemVer.V100 });
      messageProcessor = MessageProcessor;
      shutdownSignaling = ShutdownSignaling;

      log.Trace("(-)");
    }


    /// <summary>
    /// Constructs ProtoBuf message from raw data read from the network stream.
    /// </summary>
    /// <param name="Data">Raw data to be decoded to the message.</param>
    /// <returns>ProtoBuf message or null if the data do not represent a valid message.</returns>
    public override IProtocolMessage CreateMessageFromRawData(byte[] Data)
    {
      return (IProtocolMessage)LocMessageBuilder.CreateMessageFromRawData(Data);
    }

    /// <summary>
    /// Converts an IoP Network protocol message to a binary format.
    /// </summary>
    /// <param name="Message">IoP Network protocol message.</param>
    /// <returns>Binary representation of the message to be sent over the network.</returns>
    public override byte[] MessageToByteArray(IProtocolMessage Message)
    {
      return LocMessageBuilder.MessageToByteArray(Message);
    }


    /// <summary>
    /// Reads and decodes message from the stream from LOC server.
    /// </summary>
    /// <param name="CancellationToken">Cancallation token for async calls.</param>
    /// <param name="CheckProtocolViolation">If set to true, the function checks whether a protocol violation occurred and if so, it sends protocol violation error to the peer.</param>
    /// <returns>Received message of null if the function fails.</returns>
    public async Task<LocProtocolMessage> ReceiveMessageAsync(CancellationToken CancellationToken, bool CheckProtocolViolation = false)
    {
      log.Trace("()");

      LocProtocolMessage res = null;

      RawMessageResult rawMessage = await messageReader.ReceiveMessageAsync(CancellationToken);
      if (rawMessage.Data != null)
      {
        res = (LocProtocolMessage)LocMessageBuilder.CreateMessageFromRawData(rawMessage.Data);
      }
      else log.Debug("Connection to LOC server has been terminated.");

      if (CheckProtocolViolation)
      {
        if ((res == null) || rawMessage.ProtocolViolation)
          await messageProcessor.SendProtocolViolation(this);
      }

      log.Trace("(-):{0}", res != null ? "LocProtocolMessage" : "null");
      return res;
    }


    /// <summary>
    /// Sends a message to the client over the open network stream.
    /// </summary>
    /// <param name="Message">Message to send.</param>
    /// <returns>true if the connection to the client should remain open, false otherwise.</returns>
    public override async Task<bool> SendMessageAsync(IProtocolMessage Message)
    {
      log.Trace("()");

      bool res = false;
      try
      {
        res = await base.SendMessageAsync(Message);
      }
      catch (Exception e)
      {
        if (e is ObjectDisposedException)
        {
          log.Info("Connection to the LOC server has been terminated.");
        }
        else
        {
          log.Error("Exception occurred (and rethrowing): {0}", e.ToString());
          await Task.Delay(5000);
          throw e;
        }
      }

      log.Trace("(-):{0}", res);
      return res;
    }


    /// <summary>
    /// Attempts to connect to LOC server in a loop.
    /// </summary>
    /// <returns>true if the function succeeded, false if connection was established before the component shutdown.</returns>
    public override async Task<bool> ConnectAsync()
    {
      log.Trace("()");
      bool res = false;

      // Close TCP connection if it is connected and reset client.
      IPEndPoint locEndPoint = (IPEndPoint)config.Settings["LocEndPoint"];
      SetRemoteEndPoint(locEndPoint);

      while (!res && !shutdownSignaling.IsShutdown)
      {
        log.Trace("Connecting to LOC server '{0}'.", locEndPoint);
        if (await base.ConnectAsync())
        {
          messageReader = new RawMessageReader(Stream);
          res = true;
        }
        else
        {
          log.Warn("Unable to connect to LOC server '{0}', waiting 10 seconds and then retrying.", locEndPoint);

          // On Ubuntu we get exception "Sockets on this platform are invalid for use after a failed connection attempt" 
          // when we try to reconnect to the same IP:port again, after it failed for the first time. 
          // We have to close the socket and initialize it again to be able to connect.
          SetRemoteEndPoint(locEndPoint);

          try
          {
            await Task.Delay(10000, shutdownSignaling.ShutdownCancellationTokenSource.Token);
          }
          catch
          {
            // Catch cancellation exception.
          }
        }
      }

      log.Trace("(-):{0}", res);
      return res;
    }


    /// <summary>
    /// Announces profile server's primary server role interface to the LOC server.
    /// </summary>
    /// <param name="PrimaryPort">Primary port of the server.</param>
    /// <param name="Location">Optionally, an empty GpsLocation instance that will be filled with location information received from the LOC server if the function succeeds.</param>
    /// <returns>true if the function succeeds, false otherwise.</returns>
    public async Task<bool> RegisterPrimaryServerRoleAsync(int PrimaryPort, IopProtocol.GpsLocation Location = null)
    {
      log.Info("(PrimaryPort:{0})", PrimaryPort);

      bool res = false;

      ServiceInfo serviceInfo = new ServiceInfo()
      {
        Port = (uint)PrimaryPort,
        Type = ServiceType.Profile,
        ServiceData = ProtocolHelper.ByteArrayToByteString(Crypto.Sha256(((KeysEd25519)config.Settings["Keys"]).PublicKey))
      };

      LocProtocolMessage request = MessageBuilder.CreateRegisterServiceRequest(serviceInfo);
      if (await SendMessageAsync(request))
      {
        LocProtocolMessage response = await ReceiveMessageAsync(shutdownSignaling.ShutdownCancellationTokenSource.Token);
        if (response != null)
        {
          res = (response.Id == request.Id)
            && (response.MessageTypeCase == Message.MessageTypeOneofCase.Response)
            && (response.Response.Status == Status.Ok)
            && (response.Response.ResponseTypeCase == Response.ResponseTypeOneofCase.LocalService)
            && (response.Response.LocalService.LocalServiceResponseTypeCase == LocalServiceResponse.LocalServiceResponseTypeOneofCase.RegisterService);

          if (res)
          {
            if (Location != null)
            {
              IopProtocol.GpsLocation location = new IopProtocol.GpsLocation(response.Response.LocalService.RegisterService.Location.Latitude, response.Response.LocalService.RegisterService.Location.Longitude);
              Location.Latitude = location.Latitude;
              Location.Longitude = location.Longitude;
              if (Location.IsValid())
              {
                res = true;
              }
              else log.Error("Registration failed, LOC server provided invalid location information [{0:US}].", location);
            }
            else res = true;

            if (res) log.Debug("Primary interface has been registered successfully on LOC server{0}.", Location != null ? string.Format(", server location set is [{0:US}]", Location) : "");
          }
          else log.Error("Registration failed, response status is {0}.", response.Response != null ? response.Response.Status.ToString() : "n/a");
        }
        else log.Error("Invalid message received from LOC server.");
      }
      else log.Error("Unable to send register server request to LOC server.");

      log.Info("(-):{0}", res);
      return res;
    }


    /// <summary>
    /// Cancels registration of profile server's primary server role interface on the LOC server.
    /// </summary>
    /// <returns>true if the function succeeds, false otherwise.</returns>
    public async Task<bool> DeregisterPrimaryServerRoleAsync()
    {
      log.Info("()");

      bool res = false;

      LocProtocolMessage request = MessageBuilder.CreateDeregisterServiceRequest(ServiceType.Profile);
      if (await SendMessageAsync(request))
      {
        LocProtocolMessage response = await ReceiveMessageAsync(shutdownSignaling.ShutdownCancellationTokenSource.Token);
        if (response != null)
        {
          res = (response.Id == request.Id)
            && (response.MessageTypeCase == Message.MessageTypeOneofCase.Response)
            && (response.Response.Status == Status.Ok)
            && (response.Response.ResponseTypeCase == Response.ResponseTypeOneofCase.LocalService)
            && (response.Response.LocalService.LocalServiceResponseTypeCase == LocalServiceResponse.LocalServiceResponseTypeOneofCase.DeregisterService);

          if (res) log.Debug("Primary interface has been unregistered successfully on LOC server.");
          else log.Error("Deregistration failed, response status is {0}.", response.Response != null ? response.Response.Status.ToString() : "n/a");
        }
        else log.Error("Invalid message received from LOC server.");
      }
      else log.Debug("Unable to send deregister server request to LOC server.");

      log.Info("(-):{0}", res);
      return res;
    }


    /// <summary>
    /// Sends a request to the LOC server to obtain an initial neighborhood information and then reads the response.
    /// </summary>
    /// <returns>true if the function succeeds, false otherwise.</returns>
    public async Task<LocProtocolMessage> GetNeighborhoodInformationAsync()
    {
      log.Info("()");

      LocProtocolMessage res = null;
      LocProtocolMessage request = MessageBuilder.CreateGetNeighbourNodesByDistanceLocalRequest();
      if (await SendMessageAsync(request))
      {
        // Read response.
        bool responseOk = false;
        LocProtocolMessage response = await ReceiveMessageAsync(shutdownSignaling.ShutdownCancellationTokenSource.Token);
        if (response != null)
        {
          responseOk = (response.Id == request.Id)
            && (response.MessageTypeCase == Message.MessageTypeOneofCase.Response)
            && (response.Response.Status == Status.Ok)
            && (response.Response.ResponseTypeCase == Response.ResponseTypeOneofCase.LocalService)
            && (response.Response.LocalService.LocalServiceResponseTypeCase == LocalServiceResponse.LocalServiceResponseTypeOneofCase.GetNeighbourNodes);

          if (responseOk) res = response;
          else log.Error("Obtaining neighborhood information failed, response status is {0}.", response.Response != null ? response.Response.Status.ToString() : "n/a");
        }
        else log.Error("Invalid message received from LOC server.");
      }
      else log.Error("Unable to send GetNeighbourNodesByDistanceLocalRequest to LOC server.");

      log.Info("(-):{0}", res != null ? "LocProtocolMessage" : "null");
      return res;
    }

    /// <summary>
    /// Reads update messages from network stream and processes them in a loop until the connection terminates 
    /// or until an action (such as a protocol violation) that leads to termination of the connection occurs.
    /// </summary>
    public async Task ReceiveMessageLoopAsync()
    {
      log.Info("()");

      try
      {
        while (!shutdownSignaling.IsShutdown)
        {
          LocProtocolMessage message = await ReceiveMessageAsync(shutdownSignaling.ShutdownCancellationTokenSource.Token, true);
          if (message == null) break;
          bool disconnect = !await messageProcessor.ProcessMessageAsync(this, message);

          if (disconnect)
            break;
        }
      }
      catch (Exception e)
      {
        log.Error("Exception occurred: {0}", e.ToString());
      }

      log.Info("(-)");
    }
  }
}
