﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NHM.Common.Enums;
using NHM.DeviceMonitoring.TDP;
using NHMCore.ApplicationState;
using NHMCore.Configs;
using NHMCore.Mining;
using NHMCore.Nhmws.ModelsV3;
using NHMCore.Switching;
using NHMCore.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;
// static imports
using static NHMCore.Nhmws.StatusCodesV3;
using NHLog = NHM.Common.Logger;

namespace NHMCore.Nhmws
{
    static class NHWebSocketV3
    {
        #region locking

        private static readonly object _lock = new object();
        private class LockingProperty<T>
        {
            public LockingProperty(T v)
            {
                _value = v;
            }

            private T _value;
            public T Value
            {
                get
                {
                    lock (_lock)
                    {
                        return _value;
                    }
                }
                set
                {
                    lock (_lock)
                    {
                        _value = value;
                    }
                }
            }
        }
        static private LockingProperty<DateTime> _lastSendMinerStatusTimestamp = new LockingProperty<DateTime>(DateTime.MinValue);
        static private LockingProperty<DateTime?> _notifyMinerStatusAfter = new LockingProperty<DateTime?>(null);
        static private LockingProperty<bool> _isInRPC = new LockingProperty<bool>(false);

        #endregion locking

        private enum MessageType
        {
            CLOSE_WEBSOCKET = 0,
            SEND_MESSAGE,
            SEND_MESSAGE_STATUS,
        }

        private class NHSendMessage
        {
            public MessageType Type { get; private set; }
            public string Msg { get; private set; }
            public NHSendMessage(MessageType type, string msg)
            {
                Type = type;
                Msg = msg;
            }
        }

        static private bool _isNhmwsRestart = false;

        static public bool IsWsAlive => _webSocket?.ReadyState == WebSocketState.Open;
        static private WebSocket _webSocket = null;
        static private string _address;

        static private readonly LoginMessage _login = new LoginMessage
        {
            version = "NHM/" + Application.ProductVersion,
            protocol = 3
        };

        static private ConcurrentQueue<MessageEventArgs> _recieveQueue { get; set; } = new ConcurrentQueue<MessageEventArgs>();
        static private ConcurrentQueue<IEnumerable<NHSendMessage>> _sendQueue { get; set; } = new ConcurrentQueue<IEnumerable<NHSendMessage>>();


        public static void NotifyStateChanged()
        {
            // check if we are in RPC and if not send miner status
            if (!_isInRPC.Value)
            {
                _notifyMinerStatusAfter.Value = DateTime.UtcNow.AddSeconds(1);
            }
        }

        public static Task MainLoop { get; private set; } = null;

        public static void StartLoop(string address, CancellationToken token)
        {
            MainLoop = Task.Run(() => Start(address, token));
        }

        static public async Task Start(string address, CancellationToken token)
        {
            try
            {
                var random = new Random();
                _address = address;

                NHLog.Info("NHWebSocket-WD", "Starting nhmws watchdog");
                // TODO use this or just use the application exit source
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await NewConnection(token);
                        // after each connection is completed check if we should re-connect or exit the watchdog
                        // if we didn't initialize the restart delay reconnect
                        if (!_isNhmwsRestart && !token.IsCancellationRequested)
                        {
                            // delays re-connect 10 to 30 seconds
                            var delaySeconds = 10 + random.Next(0, 20);
                            NHLog.Info("NHWebSocket-WD", $"Attempting reconnect in {delaySeconds} seconds");
                            await TaskHelpers.TryDelay(TimeSpan.FromSeconds(delaySeconds), token);
                        }
                        else if (_isNhmwsRestart && !token.IsCancellationRequested)
                        {
                            NHLog.Info("NHWebSocket-WD", $"Restarting nhmws SESSION");
                        }
                    }
                    catch (TaskCanceledException e)
                    {
                        NHLog.Debug("NHWebSocket-WD", $"TaskCanceledException {e.Message}");
                        return;
                    }
                    catch (Exception e)
                    {
                        NHLog.Error("NHWebSocket-WD", $"Error occured: {e.Message}");
                    }
                }
            }
            finally
            {
                NHLog.Info("NHWebSocket-WD", "Ending nhmws watchdog");
            }
        }

        // TODO add cancelation token
        static private async Task NewConnection(CancellationToken stop)
        {
            NHLog.Info("NHWebSocket", "STARTING nhmws SESSION");
            try
            {
                // TODO think if we might want to dump prev data????
                // on each new connection clear the ConcurrentQueues, 
                _recieveQueue = new ConcurrentQueue<MessageEventArgs>();
                _sendQueue = new ConcurrentQueue<IEnumerable<NHSendMessage>>();
                _isNhmwsRestart = false;
                _notifyMinerStatusAfter.Value = null;

                NHLog.Info("NHWebSocket", "Creating socket");
                using (_webSocket = new WebSocket(_address))
                {
                    _webSocket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                    //stop.Register(() => _webSocket.Close(CloseStatusCode.Normal, "Closing CancellationToken"));
                    _webSocket.OnOpen += Login;
                    _webSocket.OnMessage += (s, eMsg) => _recieveQueue.Enqueue(eMsg);
                    _webSocket.OnError += (s, e) => NHLog.Info("NHWebSocket", $"Error occured: {e.Message}");
                    _webSocket.OnClose += (s, e) => NHLog.Info("NHWebSocket", $"Connection closed code {e.Code}: {e.Reason}"); ;
                    _webSocket.Log.Level = LogLevel.Debug;
                    _webSocket.Log.Output = (data, s) => NHLog.Info("NHWebSocket", data.ToString());
                    _webSocket.EnableRedirection = true;

                    NHLog.Info("NHWebSocket", "Connecting");
                    _webSocket.Connect();

                    const int minerStatusTickSeconds = 45;
                    var checkWaitTime = TimeSpan.FromMilliseconds(50);

                    var skipMinerStatus = !CredentialValidators.ValidateBitcoinAddress(_login.btc);

                    NHLog.Info("NHWebSocket", "Starting Loop");
                    while (IsWsAlive && !stop.IsCancellationRequested)
                    {
                        if (IsWsAlive) HandleSendMessage();
                        if (IsWsAlive) await HandleReceiveMessage();
                        // TODO add here the last miner status send check
                        if (IsWsAlive) await TaskHelpers.TryDelay(checkWaitTime, stop);

                        if (skipMinerStatus) continue;
                        var elapsedTime = DateTime.UtcNow - _lastSendMinerStatusTimestamp.Value;
                        if (elapsedTime.TotalSeconds > minerStatusTickSeconds)
                        {
                            var minerStatusJsonStr = CreateMinerStatusMessage();
                            var minerStatus = new NHSendMessage(MessageType.SEND_MESSAGE_STATUS, minerStatusJsonStr);
                            _sendQueue.Enqueue(new NHSendMessage[1] { minerStatus });
                        }
                        if (_notifyMinerStatusAfter.Value.HasValue && DateTime.UtcNow >= _notifyMinerStatusAfter.Value.Value)
                        {
                            _notifyMinerStatusAfter.Value = null;
                            var minerStatusJsonStr = CreateMinerStatusMessage();
                            var minerStatus = new NHSendMessage(MessageType.SEND_MESSAGE_STATUS, minerStatusJsonStr);
                            _sendQueue.Enqueue(new NHSendMessage[1] { minerStatus });
                        }
                    }
                    // Ws closed
                    NHLog.Info("NHWebSocket", "Exited Loop");
                }
            }
            catch (TaskCanceledException e)
            {
                NHLog.Debug("NHWebSocket", $"TaskCanceledException {e.Message}");
            }
            finally
            {
                NHLog.Info("NHWebSocket", "ENDING nhmws SESSION");
                ApplicationStateManager.SetNhmwsConnectionChanged(false);
            }
        }

        private static void Send(string data)
        {
            NHLog.Info("NHWebSocket", $"Sending data: {data}");
            _webSocket?.Send(data);
        }

        static private void HandleSendMessage()
        {
            var ok = _sendQueue.TryDequeue(out var sendMsgCommands);
            if (!ok) return;
            foreach (var msg in sendMsgCommands)
            {
                if (msg == null) continue;

                var data = msg.Msg;
                switch (msg.Type)
                {
                    case MessageType.CLOSE_WEBSOCKET:
                        _webSocket?.Close(CloseStatusCode.Normal, data);
                        _isNhmwsRestart = true;
                        break;
                    case MessageType.SEND_MESSAGE:
                    case MessageType.SEND_MESSAGE_STATUS:
                        Send(data);
                        if (MessageType.SEND_MESSAGE_STATUS == msg.Type)
                        {
                            _lastSendMinerStatusTimestamp.Value = DateTime.UtcNow;
                        }
                        break;
                    default:
                        // TODO throw if we get here
                        break;
                }
            }
        }

        static private async Task HandleReceiveMessage()
        {
            if (_recieveQueue.TryDequeue(out var recieveMsg))
            {
                await HandleMessage(recieveMsg);
            }
        }


        static private void Login(object sender, EventArgs e)
        {
            NHLog.Info("NHWebSocket", "Connected");
            ApplicationStateManager.SetNhmwsConnectionChanged(true);
            try
            {
                // always send login
                var loginJson = JsonConvert.SerializeObject(_login);
                var sendMessages = new List<NHSendMessage> { new NHSendMessage(MessageType.SEND_MESSAGE, loginJson) };
                if (CredentialValidators.ValidateBitcoinAddress(_login.btc))
                {
                    var minerStatusJsonStr = CreateMinerStatusMessage(true);
                    sendMessages.Add(new NHSendMessage(MessageType.SEND_MESSAGE_STATUS, minerStatusJsonStr));
                }
                _sendQueue.Enqueue(sendMessages);
            }
            catch (Exception er)
            {
                NHLog.Info("NHWebSocket", er.Message);
            }
        }

        static public void ResetCredentials(string btc = null, string worker = null, string group = null)
        {
            // TODO check protocol
            // send status first and re-set credentials
            var minerStatusJsonStr = CreateMinerStatusMessage();
            var minerStatus = new NHSendMessage(MessageType.SEND_MESSAGE_STATUS, minerStatusJsonStr);
            _sendQueue.Enqueue(new NHSendMessage[1] { minerStatus });
            // TODO check 
            SetCredentials(btc, worker, group);
        }

        static public void SetCredentials(string btc = null, string worker = null, string group = null)
        {
            _login.rig = ApplicationStateManager.RigID();
            if (btc != null) _login.btc = btc;
            if (worker != null) _login.worker = worker;
            if (group != null) _login.group = group;
            // on credentials change always send close websocket message
            var closeMsg = new NHSendMessage(MessageType.CLOSE_WEBSOCKET, $"Credentials change reconnecting {ApplicationStateManager.Title}.");
            _sendQueue.Enqueue(new NHSendMessage[1] { closeMsg });
        }

        #region Message handling

        private static string CreateMinerStatusMessage(bool sendDeviceNames = false)
        {
            var devices = AvailableDevices.Devices;
            var rigStatus = ApplicationStateManager.CalcRigStatusString();
            var paramList = new List<JToken>
            {
                rigStatus
            };

            var deviceList = new JArray();
            foreach (var device in devices)
            {
                try
                {
                    var array = new JArray
                    {
                        sendDeviceNames ? device.Name : "",
                        device.B64Uuid  // TODO
                    };
                    var status = DeviceReportStatus(device.DeviceType, device.State);
                    array.Add(status);

                    array.Add((int)Math.Round(device.Load));

                    var speedsJson = new JArray();
                    var speeds = MiningDataStats.GetSpeedForDevice(device.Uuid);
                    if (speeds != null && device.State == DeviceState.Mining)
                    {
                        foreach (var kvp in speeds)
                        {
                            speedsJson.Add(new JArray((int)kvp.type, kvp.speed));
                        }
                    }
                    array.Add(speedsJson);

                    // Hardware monitoring
                    array.Add((int)Math.Round(device.Temp));
                    array.Add(device.FanSpeedRPM);
                    array.Add((int)Math.Round(device.PowerUsage));

                    // Power mode
                    array.Add((int)device.TDPSimple);

                    // Intensity mode
                    array.Add(0);

                    // fan speed percentage
                    array.Add(device.FanSpeed);

                    deviceList.Add(array);
                }
                catch (Exception e)
                {
                    NHLog.Error("NHWebSocket", e.Message);
                }
            }

            paramList.Add(deviceList);

            var data = new MinerStatusMessage
            {
                param = paramList
            };
            var sendData = JsonConvert.SerializeObject(data);

            return sendData;
        }


        static private async Task HandleMessage(MessageEventArgs e)
        {
            try
            {
                if (e.IsText)
                {
                    NHLog.Info("NHWebSocket", $"Received: {e.Data}");
                    var method = GetMethod(e.Data);
                    if (IsRpcMethod(method))
                    {
                        await HandleRpcMessage(method, e.Data);
                    }
                    else
                    {
                        await HandleNonRpcMessage(method, e.Data);
                    }
                }
            }
            catch (Exception ex)
            {
                NHLog.Error("NHWebSocket", $"HandleMessage {ex.Message}");
            }
        }

        // TODO copy pasted crap from NiceHashStats
        #region NonRpcMessages
        #region SMA
        private static void SetAlgorithmRates(JArray data)
        {
            try
            {
                var payingDict = new Dictionary<AlgorithmType, double>();
                if (data != null)
                {
                    foreach (var algo in data)
                    {
                        var algoKey = (AlgorithmType)algo[0].Value<int>();
                        payingDict[algoKey] = algo[1].Value<double>();
                    }
                }

                NHSmaData.UpdateSmaPaying(payingDict);
                // TODO new check crap 
                foreach (var dev in AvailableDevices.Devices)
                {
                    dev.UpdateEstimatePaying(payingDict);
                }
            }
            catch (Exception e)
            {
                NHLog.Error("NHWebSocket", $"SetAlgorithmRates error: {e.Message}");
            }
        }
        private static Task HandleSMAMessage(string data)
        {
            dynamic message = JsonConvert.DeserializeObject(data);

            // Try in case stable is not sent, we still get updated paying rates
            try
            {
                JArray stable = JsonConvert.DeserializeObject(message.stable.Value);
                //SetStableAlgorithms(stable);
                var stables = stable.Select(algo => (AlgorithmType)algo.Value<int>());
                NHSmaData.UpdateStableAlgorithms(stables);
            }
            catch
            { }
            SetAlgorithmRates(message.data);
            return Task.CompletedTask;
        }
        #endregion SMA
        #region MARKETS

        private static Task HandleMarkets(string data)
        {
            try
            {
            }
            catch (Exception e)
            {
                NHLog.Error("NHWebSocket", $"HandleMarkets error: {e.Message}");
            }
            return Task.CompletedTask;
        }
        #endregion MARKETS

        #region BALANCE
        private static Task SetBalance(string data)
        {
            try
            {
                dynamic message = JsonConvert.DeserializeObject(data);
                string balance = message.value.Value;
                if (double.TryParse(balance, NumberStyles.Float, CultureInfo.InvariantCulture, out var btcBalance))
                {
                    BalanceAndExchangeRates.Instance.BtcBalance = btcBalance;
                }
            }
            catch (Exception e)
            {
                NHLog.Error("NHWebSocket", $"SetBalance error: {e.Message}");
            }
            return Task.CompletedTask;
        }
        #endregion BALANCE

        #region BRUN
        private static Task HandleBurn(string data)
        {
            try
            {
                dynamic message = JsonConvert.DeserializeObject(data);
                ApplicationStateManager.Burn(message.message.Value);
            }
            catch (Exception e)
            {
                NHLog.Error("NHWebSocket", $"SetBalance error: {e.Message}");
            }
            return Task.CompletedTask;
        }
        #endregion BRUN

        #region VERSION
        private static Task SetVersion(string data)
        {
            dynamic message = JsonConvert.DeserializeObject(data);
            string version = message.v3.Value;
            VersionState.Instance.OnVersionUpdate(version);
            return Task.CompletedTask;
        }
        #endregion VERSION

        #region EXCHANGE_RATES
        private static Task SetExchangeRates(string origdata)
        {
            try
            {
                dynamic message = JsonConvert.DeserializeObject(origdata);
                string data = message.data.Value;
                var exchange = JsonConvert.DeserializeObject<ExchangeRateJson>(data);
                if (exchange?.exchanges_fiat == null || exchange.exchanges == null) return Task.CompletedTask;
                double usdBtcRate = -1;
                foreach (var exchangePair in exchange.exchanges)
                {
                    if (!exchangePair.TryGetValue("coin", out var coin) || coin != "BTC" ||
                        !exchangePair.TryGetValue("USD", out var usd) ||
                        !double.TryParse(usd, NumberStyles.Float, CultureInfo.InvariantCulture, out var usdD))
                        continue;

                    usdBtcRate = usdD;
                    break;
                }
                BalanceAndExchangeRates.Instance.UpdateExchangesFiat(usdBtcRate, exchange.exchanges_fiat);
            }
            catch (Exception e)
            {
                NHLog.Error("NHWebSocket", $"SetExchangeRates error: {e.Message}");
            }
            return Task.CompletedTask;
        }
        #endregion EXCHANGE_RATES

        #endregion NonRpcMessages

        static private Task HandleNonRpcMessage(string method, string data)
        {
            return method switch
            {
                "sma" => HandleSMAMessage(data),
                "markets" => HandleMarkets(data),
                "balance" => SetBalance(data),
                "versions" => SetVersion(data),
                "burn" => HandleBurn(data),
                "exchange_rates" => SetExchangeRates(data),
                _ => throw new Exception($"NonRpcMessage operation not supported for method '{method}'"),
            };
        }

        #region RpcMessages

        private class RpcExecutionResult
        {
            public bool LoginNeeded { get; set; }
            public bool Id { get; set; }
            public bool Success { get; set; }
        }

        private static void ThrowIfWeCannotHanldeRPC()
        {
            // throw if pending
            if (ApplicationStateManager.CalcRigStatus() == RigStatus.Pending)
            {
                throw new RpcException($"Cannot handle RPC call Rig is in PENDING state.", ErrorCodeV3.UnableToHandleRpc);
            }
        }


        #region Credentials setters (btc/username, worker, group)
        private static async Task<bool> miningSetUsername(string btc)
        {
            var userSetResult = await ApplicationStateManager.SetBTCIfValidOrDifferent(btc, true);
            switch (userSetResult)
            {
                case ApplicationStateManager.SetResult.INVALID:
                    throw new RpcException("Bitcoin address invalid", ErrorCodeV3.InvalidUsername);
                case ApplicationStateManager.SetResult.CHANGED:
                    // we return executed
                    return true;
                case ApplicationStateManager.SetResult.NOTHING_TO_CHANGE:
                    throw new RpcException($"Nothing to change btc \"{btc}\" already set", ErrorCodeV3.RedundantRpc);
                default:
                    throw new RpcException($"", ErrorCodeV3.InternalNhmError);
            }
            
        }

        private static bool miningSetWorker(string worker)
        {
            var workerSetResult = ApplicationStateManager.SetWorkerIfValidOrDifferent(worker, true);
            switch (workerSetResult)
            {
                case ApplicationStateManager.SetResult.INVALID:
                    throw new RpcException("Worker name invalid", ErrorCodeV3.InvalidWorker);
                case ApplicationStateManager.SetResult.CHANGED:
                    // we return executed
                    return true;
                case ApplicationStateManager.SetResult.NOTHING_TO_CHANGE:
                    throw new RpcException($"Nothing to change worker name \"{worker}\" already set", ErrorCodeV3.RedundantRpc);
                default:
                    throw new RpcException($"", ErrorCodeV3.InternalNhmError);
            }
        }

        private static bool miningSetGroup(string group)
        {
            var groupSetResult = ApplicationStateManager.SetGroupIfValidOrDifferent(group, true);
            switch (groupSetResult)
            {
                case ApplicationStateManager.SetResult.INVALID:
                    // TODO error code not correct
                    throw new RpcException("Group name invalid", ErrorCodeV3.UnableToHandleRpc);
                case ApplicationStateManager.SetResult.CHANGED:
                    // we return executed
                    return true;
                case ApplicationStateManager.SetResult.NOTHING_TO_CHANGE:
                    throw new RpcException($"Nothing to change group \"{group}\" already set", ErrorCodeV3.RedundantRpc);
                default:
                    throw new RpcException($"", ErrorCodeV3.InternalNhmError);
            }
        }
        #endregion Credentials setters (btc/username, worker, group)

        private static async Task<bool> SetDevicesEnabled(string devs, bool enabled)
        {
            bool allDevices = devs == "*";
            // get device with uuid if it exists, devs can be single device uuid
            var deviceWithUUID = AvailableDevices.GetDeviceWithUuidOrB64Uuid(devs);

            // Check if RPC should execute
            // check if redundant rpc
            if (allDevices && enabled && AvailableDevices.IsEnableAllDevicesRedundantOperation())
            {
                throw new RpcException("All devices are already enabled.", ErrorCodeV3.RedundantRpc);
            }
            // all disable
            if (allDevices && !enabled && AvailableDevices.IsDisableAllDevicesRedundantOperation())
            {
                throw new RpcException("All devices are already disabled.", ErrorCodeV3.RedundantRpc);
            }
            // if single and doesn't exist
            if (!allDevices && deviceWithUUID == null)
            {
                throw new RpcException("Device not found", ErrorCodeV3.NonExistentDevice);
            }
            // if we have the device but it is redundant
            if (!allDevices && deviceWithUUID.IsDisabled == !enabled)
            {
                var stateStr = enabled ? "enabled" : "disabled";
                throw new RpcException($"Devices with uuid {devs} is already {stateStr}.", ErrorCodeV3.RedundantRpc);
            }

            // if got here than we can execute the call
            await ApplicationStateManager.SetDeviceEnabledState(null, (devs, enabled));
            // TODO invoke the event for controls that use it
            //OnDeviceUpdate?.Invoke(null, new DeviceUpdateEventArgs(AvailableDevices.Devices.ToList()));
            return true;
        }

        #region Start
        private static async Task<bool> startMiningAllDevices()
        {
            var allDisabled = AvailableDevices.Devices.All(dev => dev.IsDisabled);
            if (allDisabled)
            {
                throw new RpcException("All devices are disabled cannot start", ErrorCodeV3.DisabledDevice);
            }
            var (success, msg) = await ApplicationStateManager.StartAllAvailableDevicesTask();
            if (!success)
            {
                throw new RpcException(msg, ErrorCodeV3.RedundantRpc);
            }
            return true;
        }

        private static async Task<bool> startMiningOnDeviceWithUuid(string uuid)
        {
            string errMsgForUuid = $"Cannot start device with uuid {uuid}";
            // get device with uuid if it exists, devs can be single device uuid
            var deviceWithUUID = AvailableDevices.GetDeviceWithUuidOrB64Uuid(uuid);
            if (deviceWithUUID == null)
            {
                throw new RpcException($"{errMsgForUuid}. Device not found.", ErrorCodeV3.NonExistentDevice);
            }
            if (deviceWithUUID.IsDisabled)
            {
                throw new RpcException($"{errMsgForUuid}. Device is disabled.", ErrorCodeV3.DisabledDevice);
            }
            var (success, msg) = await ApplicationStateManager.StartDeviceTask(deviceWithUUID);
            if (!success)
            {
                // TODO this can also be an error
                throw new RpcException($"{errMsgForUuid}. {msg}.", ErrorCodeV3.RedundantRpc);
            }
            return true;
        }

        private static Task<bool> StartMining(string devs)
        {
            if (devs == "*") return startMiningAllDevices();
            return startMiningOnDeviceWithUuid(devs);
        }
        #endregion Start

        #region Stop
        private static async Task<bool> stopMiningAllDevices()
        {
            var allDisabled = AvailableDevices.Devices.All(dev => dev.IsDisabled);
            if (allDisabled)
            {
                throw new RpcException("All devices are disabled cannot stop", ErrorCodeV3.DisabledDevice);
            }
            var (success, msg) = await ApplicationStateManager.StopAllDevicesTask();
            if (!success)
            {
                throw new RpcException(msg, ErrorCodeV3.RedundantRpc);
            }
            return success;
        }

        private static async Task<bool> stopMiningOnDeviceWithUuid(string uuid)
        {
            string errMsgForUuid = $"Cannot stop device with uuid {uuid}";
            // get device with uuid if it exists, devs can be single device uuid
            var deviceWithUUID = AvailableDevices.GetDeviceWithUuidOrB64Uuid(uuid);
            if (deviceWithUUID == null)
            {
                throw new RpcException($"{errMsgForUuid}. Device not found.", ErrorCodeV3.NonExistentDevice);
            }
            if (deviceWithUUID.IsDisabled)
            {
                throw new RpcException($"{errMsgForUuid}. Device is disabled.", ErrorCodeV3.DisabledDevice);
            }
            var (success, msg) = await ApplicationStateManager.StopDeviceTask(deviceWithUUID);
            if (!success)
            {
                // TODO this can also be an error
                throw new RpcException($"{errMsgForUuid}. {msg}.", ErrorCodeV3.RedundantRpc);
            }
            return success;
        }

        private static Task<bool> StopMining(string devs)
        {
            if (devs == "*") return stopMiningAllDevices();
            return stopMiningOnDeviceWithUuid(devs);
        }
        #endregion Stop

        private static void SetPowerMode(string device, TDPSimpleType level)
        {
            if (GlobalDeviceSettings.Instance.DisableDevicePowerModeSettings) throw new RpcException("Not able to set Power Mode: Device Power Mode Settings Disabled", ErrorCodeV3.UnableToHandleRpc);

            var devs = device == "*" ?
                AvailableDevices.Devices :
                AvailableDevices.Devices.Where(d => d.B64Uuid == device);

            var found = devs.Count() > 0;
            var hasEnabled = false;
            var setSuccess = new List<(bool success, DeviceType type)>();
            foreach (var dev in devs)
            {
                if (!dev.Enabled) continue;
                if (!dev.CanSetPowerMode) continue;
                hasEnabled = true;
                // TODO check if set
                var result = dev.SetPowerMode(level);
                setSuccess.Add((result, dev.DeviceType));
            }

            if (!setSuccess.All(t => t.success))
            {
                if (setSuccess.Any(res => res.type == DeviceType.NVIDIA && !Helpers.IsElevated && !res.success))
                {
                    throw new RpcException("Not able to set power modes for devices: Must start NiceHashMiner as Admin", ErrorCodeV3.UnableToHandleRpc);
                }
                throw new RpcException("Not able to set power modes for all devices", ErrorCodeV3.UnableToHandleRpc);
            }

            if (found && !hasEnabled)
            {
                throw new RpcException("No settable devices found", ErrorCodeV3.UnableToHandleRpc);
            }

            if (!found)
            {
                throw new RpcException("No settable devices found", ErrorCodeV3.UnableToHandleRpc);
            }
        }

        private static async Task<string> MinerReset(string level)
        {
            string appBurn()
            {
                _ = HandleBurn("MinerReset app burn called");
                return "";
            }
            string rigRestart()
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3 * 1000);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "-r -f -t 0",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var reboot = Process.Start(startInfo);
                    reboot.WaitForExit();
                });
                return "";
            }
            async Task<string> systemDump()
            {
                var result = await Task.Run(async () => await Helpers.CreateAndUploadLogReport());
                return result.isUploaded ? result.uploadUrl : "";
            }
            return level switch
            {
                "app burn" => appBurn(),
                "rig restart" => rigRestart(),
                "system dump" => await systemDump(),
                _ => throw new RpcException($"RpcMessage MinerReset operation not supported for level '{level}'", ErrorCodeV3.UnableToHandleRpc),
            };
        }

        #endregion RpcMessages

        static private async Task HandleRpcMessage(string method, string data)
        {
            string btc = null;
            string worker = null;
            string group = null;
            int rpcId = -1;
            bool executed = false;
            bool loginNeeded = false;
            ExecutedCall executedCall = null;
            string rpcAnswer = "";
            try
            {
                _isInRPC.Value = true;
                dynamic message = JsonConvert.DeserializeObject(data);
                rpcId = (int?)message.id ?? -1;

                ThrowIfWeCannotHanldeRPC();
                switch (method)
                {
                    case "mining.set.username":
                        btc = (string)message.username;
                        executed = await miningSetUsername(btc);
                        loginNeeded = executed;
                        break;
                    case "mining.set.worker":
                        worker = (string)message.worker;
                        executed = miningSetWorker(worker);
                        loginNeeded = executed;
                        break;
                    case "mining.set.group":
                        group = (string)message.group;
                        executed = miningSetGroup(group);
                        loginNeeded = executed;
                        break;
                    case "mining.enable":
                        executed = await SetDevicesEnabled((string)message.device, true);
                        break;
                    case "mining.disable":
                        executed = await SetDevicesEnabled((string)message.device, false);
                        break;
                    case "mining.start":
                        executed = await StartMining((string)message.device);
                        break;
                    case "mining.stop":
                        executed = await StopMining((string)message.device);
                        break;
                    case "mining.set.power_mode":
                        // TODO not supported atm
                        SetPowerMode((string)message.device, (TDPSimpleType)message.power_mode);
                        break;
                    case "miner.reset":
                        rpcAnswer = await MinerReset((string)message.level);
                        break;
                    default:
                        throw new RpcException($"RpcMessage operation not supported for method '{method}'", ErrorCodeV3.UnableToHandleRpc);
                }
                var rpcAnswerReturn = !string.IsNullOrEmpty(rpcAnswer) ? rpcAnswer : null;
                executedCall = new ExecutedCall(rpcId, 0, rpcAnswerReturn);
            }
            catch (RpcException rpcEx)
            {
                executedCall = new ExecutedCall(rpcId, rpcEx.Code, rpcEx.Message);
            }
            catch (Exception e)
            {
                NHLog.Error("NHWebSocket", $"Non RpcException - error: {e.Message}");
                // intenral nhm error
                if (executedCall == null) executedCall = new ExecutedCall(rpcId, 1, "Internal NiceHash Miner Error");
            }
            finally
            {
                _isInRPC.Value = false;
                if (executedCall != null)
                {
                    // SEND ONLY WHEN status changed
                    // send miner status and send executed
                    var minerStatusMsg = CreateMinerStatusMessage();
                    Send(minerStatusMsg);
                    _lastSendMinerStatusTimestamp.Value = DateTime.UtcNow;
                    // Then executed
                    var rpcMessage = executedCall.Serialize();
                    Send(rpcMessage);
                    // Login if we have to
                    if (loginNeeded)
                    {
                        SetCredentials(btc, worker, group);
                    }
                }
            }
        }

        private static string GetMethod(string data)
        {
            dynamic message = JsonConvert.DeserializeObject(data);
            return message.method.Value as string;
        }

        private static bool IsRpcMethod(string method)
        {
            // well pretty much all RPCs start with mining.*
            switch (method)
            {
                case "mining.set.username":
                case "mining.set.worker":
                case "mining.set.group":
                case "mining.enable":
                case "mining.disable":
                case "mining.start":
                case "mining.stop":
                case "mining.set.power_mode":
                case "miner.reset":
                    return true;
                default:
                    return false;
            }
        }
        #endregion Message handling

    }
}
