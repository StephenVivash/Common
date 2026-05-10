namespace WattCycle.Core;

internal sealed record WattCycleScanCandidate(
    ulong BluetoothAddress,
    string Name,
    short Rssi,
    bool ServiceAdvertised,
    DateTimeOffset LastSeen)
{
    public WattCycleDeviceAdvertisement ToAdvertisement() =>
        new(BluetoothAddress, Name, Rssi, ServiceAdvertised);
}
