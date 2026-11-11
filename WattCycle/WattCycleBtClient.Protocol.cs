#if WINDOWS || ANDROID || IOS || MACCATALYST || WATTCYCLE_PROTOCOL_TESTS
namespace WattCycle.Core;

public sealed partial class WattCycleBtClient
{
    private JbdFrameReader _frameReader = new();
    private TdtFrameReader _tdtFrameReader = new();
    private PendingCommand? _pendingCommand;
    private PendingTdtCommand? _pendingTdtCommand;
    private byte _tdtCommandHead = 0x1e;
    private BmsProtocolKind _protocolKind = BmsProtocolKind.Jbd;
    public Task<WattCycleBatteryReading> ReadOnceAsync(WattCycleDeviceAdvertisement discovered,
        CancellationToken cancellationToken = default) => BluetoothReadAttempt.RunAsync(
            () => ConnectForCommandAsync(discovered, cancellationToken),
            () => ReadBatteryAsync(cancellationToken),
            async () =>
            {
                OnDiagnostic("Read attempt cleanup started.");
                await CleanupConnectionAsync();
                OnDiagnostic("Read attempt cleanup completed.");
            }, cancellationToken);
    public async ValueTask DisposeAsync() => await CleanupConnectionAsync();

    private static bool LooksLikeBatteryName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith("XDZN", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WT", StringComparison.OrdinalIgnoreCase));

    public async Task ConnectAndPollAsync(WattCycleDeviceAdvertisement discovered, CancellationToken cancellationToken = default)
    {
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (reconnectAttempt > 0)
                {
                    OnDiagnostic($"Reconnect attempt {reconnectAttempt} to {discovered.DisplayName}...");
                }

                await ConnectAndPollOnceAsync(discovered, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                OnInfo($"WattCycle BT poll failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                await CleanupConnectionAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            ConnectionStatusChanged?.Invoke(this, "Reconnecting");
            OnDiagnostic($"Reconnecting to {discovered.DisplayName} in {delay.TotalSeconds:F1}s...");
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async Task<WattCycleMosControlResult> SetMosAsync(
        WattCycleDeviceAdvertisement discovered,
        bool chargeEnabled,
        bool dischargeEnabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ConnectForCommandAsync(discovered, cancellationToken);
            // A successful read establishes the working TDT header before any write.
            var before = await ReadBatteryAsync(cancellationToken);
            OnDiagnostic($"MOS before: charge={before.ChargeMosEnabled}, discharge={before.DischargeMosEnabled}");
            await SendMosControlAsync(chargeEnabled, dischargeEnabled, cancellationToken);
            await Task.Delay(250, cancellationToken);
            var reading = await ReadBatteryAsync(cancellationToken);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var matches = reading.ChargeMosEnabled == chargeEnabled && reading.DischargeMosEnabled == dischargeEnabled;
                OnDiagnostic($"MOS readback {attempt}/3: charge={reading.ChargeMosEnabled}, discharge={reading.DischargeMosEnabled}, matches={matches}");
                if (matches || attempt == 3)
                {
                    break;
                }
                await Task.Delay(500, cancellationToken);
                reading = await ReadBatteryAsync(cancellationToken);
            }
            return new WattCycleMosControlResult(
                reading,
                reading.ChargeMosEnabled == chargeEnabled,
                reading.DischargeMosEnabled == dischargeEnabled);
        }
        finally
        {
            await CleanupConnectionAsync();
        }
    }

    private async Task<WattCycleBatteryReading> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            var statusFrame = await SendTdtReadCommandAsync(TdtProtocol.StatusCommand, cancellationToken);
            var protectionFrame = await SendTdtReadCommandAsync(TdtProtocol.ProtectionCommand, cancellationToken);
            OnDiagnostic(TdtProtocol.DescribeStatusFields(statusFrame, protectionFrame));
            if (TdtProtocol.TryBuildReading(statusFrame, protectionFrame, out var reading))
            {
                return reading;
            }

            throw new InvalidOperationException($"Could not decode TDT BMS info: status={ToHexFrame(statusFrame)}, protection={ToHexFrame(protectionFrame)}");
        }

        var basicFrame = await SendReadCommandAsync(WattCycleBluetoothConstants.BasicInfoRegister, cancellationToken);
        if (!JbdProtocol.TryParseBasicInfo(basicFrame, out var basic))
        {
            throw new InvalidOperationException($"Could not decode basic BMS info: {ToHexFrame(basicFrame)}");
        }

        var cellFrame = await SendReadCommandAsync(WattCycleBluetoothConstants.CellVoltagesRegister, cancellationToken);
        if (!JbdProtocol.TryParseCellVoltages(cellFrame, out var cellVoltages))
        {
            OnDiagnostic($"Could not decode cell voltages: {ToHexFrame(cellFrame)}");
            cellVoltages = Array.Empty<double>();
        }

        return new WattCycleBatteryReading(
            DateTimeOffset.Now,
            basic.PackVoltage,
            basic.Current,
            basic.RemainingCapacityAh,
            basic.NominalCapacityAh,
            basic.StateOfChargePercent,
            basic.CycleCount,
            basic.ChargeMosEnabled,
            basic.DischargeMosEnabled,
            cellVoltages,
            basic.TemperaturesCelsius,
            basic.ProtectionStatus);
    }

    private async Task SendMosControlAsync(bool chargeEnabled, bool dischargeEnabled, CancellationToken cancellationToken)
    {
        if (_protocolKind == BmsProtocolKind.Tdt)
        {
            OnDiagnostic($"Sending TDT MOS control: charge={chargeEnabled}, discharge={dischargeEnabled}, head=0x{_tdtCommandHead:X2}");
            await SendTdtMosSwitchAsync(chargeSwitch: true, enabled: chargeEnabled, cancellationToken);
            await SendTdtMosSwitchAsync(chargeSwitch: false, enabled: dischargeEnabled, cancellationToken);
            return;
        }

        var mosMask = (byte)((chargeEnabled ? 0x01 : 0x00) | (dischargeEnabled ? 0x02 : 0x00));
        OnDiagnostic($"Sending MOS control: charge={chargeEnabled}, discharge={dischargeEnabled}, mask=0x{mosMask:X2}, protocol={_protocolKind}");
        await SendJbdWriteCommandAsync(WattCycleBluetoothConstants.MosControlRegister, [0x00, mosMask], cancellationToken);
    }

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 4)));
        var seconds = Math.Min(
            WattCycleBluetoothConstants.AutoReconnectMaxDelay.TotalSeconds,
            WattCycleBluetoothConstants.AutoReconnectInitialDelay.TotalSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private void HandleNotification(byte[] bytes)
    {
        OnDiagnostic($"Notify: {Convert.ToHexString(bytes)}");
        foreach (var rawFrame in _frameReader.Add(bytes))
        {
            if (!JbdProtocol.TryParseFrame(rawFrame, out var frame))
            {
                OnDiagnostic($"Invalid BMS frame: {Convert.ToHexString(rawFrame)}");
                continue;
            }

            if (_pendingCommand?.Register == frame.Register)
            {
                _pendingCommand.Completion.TrySetResult(frame);
            }
        }

        foreach (var rawFrame in _tdtFrameReader.Add(bytes))
        {
            if (!TdtProtocol.TryParseFrame(rawFrame, out var frame))
            {
                OnDiagnostic($"Invalid TDT frame: {Convert.ToHexString(rawFrame)}");
                continue;
            }

            if (_pendingTdtCommand is { } pending && pending.Command == frame.Command &&
                (frame.Function == pending.Function || frame.Function == (pending.Function | 0x80)))
            {
                pending.Completion.TrySetResult(frame);
            }
        }
    }
    private static string ToHexFrame(JbdFrame frame) => $"register=0x{frame.Register:X2}, status=0x{frame.Status:X2}, payload={Convert.ToHexString(frame.Payload)}";
    private static string ToHexFrame(TdtFrame frame) => $"command=0x{frame.Command:X2}, payload={Convert.ToHexString(frame.Payload)}";
    private void OnDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);
    private void OnInfo(string message) => InfoMessage?.Invoke(this, message);
    private sealed record PendingCommand(byte Register, TaskCompletionSource<JbdFrame> Completion);
    private sealed record PendingTdtCommand(byte Command, TaskCompletionSource<TdtFrame> Completion, byte Function = TdtProtocol.ReadFunction);
    private async Task<TdtFrame> SendTdtReadCommandAsync(byte command, CancellationToken cancellationToken)
    {
        foreach (var head in new byte[] { _tdtCommandHead, _tdtCommandHead == 0x1e ? (byte)0x7e : (byte)0x1e })
        {
            var pending = new PendingTdtCommand(command, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingTdtCommand = pending;
            try
            {
                var bytes = TdtProtocol.BuildReadCommand(command, head);
                OnDiagnostic($"TDT read: {Convert.ToHexString(bytes)}");
                await WriteCommandAsync(bytes, true, cancellationToken);
                TdtFrame response;
                try
                {
                    response = await pending.Completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    OnDiagnostic($"TDT read 0x{command:X2} timed out with head=0x{head:X2}.");
                    continue;
                }
                if (response.Function != TdtProtocol.ReadFunction)
                    throw new InvalidOperationException($"TDT read rejected: {ToHexFrame(response)}, function=0x{response.Function:X2}");
                _tdtCommandHead = head;
                return response;
            }
            finally
            {
                if (ReferenceEquals(_pendingTdtCommand, pending)) _pendingTdtCommand = null;
            }
        }
        throw new TimeoutException($"TDT read command 0x{command:X2} timed out.");
    }

    private async Task<JbdFrame> SendReadCommandAsync(byte register, CancellationToken cancellationToken)
    {
        var pending = new PendingCommand(register, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _pendingCommand = pending;
        try
        {
            var bytes = JbdProtocol.BuildReadCommand(register);
            OnDiagnostic($"JBD read: {Convert.ToHexString(bytes)}");
            await WriteCommandAsync(bytes, false, cancellationToken);
            try
            {
                return await pending.Completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return await TryReadNotifyCharacteristicAsync(register, cancellationToken) ?? throw new TimeoutException($"JBD register 0x{register:X2} timed out.");
            }
        }
        finally
        {
            if (ReferenceEquals(_pendingCommand, pending)) _pendingCommand = null;
        }
    }

    private async Task SendTdtMosSwitchAsync(bool chargeSwitch, bool enabled, CancellationToken cancellationToken)
    {
        var command = chargeSwitch ? TdtProtocol.ChargeSwitchCommand : TdtProtocol.DischargeSwitchCommand;
        var pending = new PendingTdtCommand(command, new(TaskCreationOptions.RunContinuationsAsynchronously), TdtProtocol.WriteFunction);
        _pendingTdtCommand = pending;
        try
        {
            var bytes = TdtProtocol.BuildMosSwitchCommand(chargeSwitch, enabled, _tdtCommandHead);
            OnDiagnostic($"TDT switch: {Convert.ToHexString(bytes)}");
            await WriteCommandAsync(bytes, true, cancellationToken);
            TdtFrame response;
            try
            {
                response = await pending.Completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                OnDiagnostic($"TDT switch 0x{command:X2}: no acknowledgement; outcome unknown until readback. No write retry.");
                return;
            }
            if (!TdtProtocol.IsSuccessfulWriteResponse(response))
                throw new InvalidOperationException($"TDT switch rejected: {ToHexFrame(response)}, function=0x{response.Function:X2}. Earlier switch changes may have applied.");
        }
        finally
        {
            if (ReferenceEquals(_pendingTdtCommand, pending)) _pendingTdtCommand = null;
        }
    }

    private async Task<JbdFrame> SendJbdWriteCommandAsync(byte register, byte[] payload, CancellationToken cancellationToken)
    {
        var pending = new PendingCommand(register, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _pendingCommand = pending;
        try
        {
            var bytes = JbdProtocol.BuildWriteCommand(register, payload);
            OnDiagnostic($"JBD write: {Convert.ToHexString(bytes)}");
            await WriteCommandAsync(bytes, false, cancellationToken);
            JbdFrame response;
            try
            {
                response = await pending.Completion.Task.WaitAsync(WattCycleBluetoothConstants.CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                OnDiagnostic($"JBD write 0x{register:X2}: no acknowledgement; verifying by readback. No write retry.");
                return new JbdFrame(register, 0xff, []);
            }
            if (response.Status != 0) throw new InvalidOperationException($"JBD write rejected: {ToHexFrame(response)}");
            return response;
        }
        finally
        {
            if (ReferenceEquals(_pendingCommand, pending)) _pendingCommand = null;
        }
    }

}
#endif
