namespace WattCycle.Core;

public static class WattCycleBluetoothConstants
{
	public static readonly TimeSpan AutoReconnectInitialDelay = TimeSpan.FromSeconds(2);
	public static readonly TimeSpan AutoReconnectMaxDelay = TimeSpan.FromSeconds(20);
	public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

	public static readonly Guid JbdServiceUuid = BleUuid(0xff00);
	public static readonly Guid NotifyCharacteristicUuid = BleUuid(0xff01);
	public static readonly Guid WriteCharacteristicUuid = BleUuid(0xff02);
	public static readonly Guid Fff0ServiceUuid = BleUuid(0xfff0);
	public static readonly Guid Fff1CharacteristicUuid = BleUuid(0xfff1);
	public static readonly Guid Fff2CharacteristicUuid = BleUuid(0xfff2);
	public static readonly Guid FffaCharacteristicUuid = BleUuid(0xfffa);
	public static readonly Guid Ffe0ServiceUuid = BleUuid(0xffe0);
	public static readonly Guid Ffe1CharacteristicUuid = BleUuid(0xffe1);

	internal static readonly IReadOnlyList<BmsProtocolDefinition> Protocols =
	[
		new("JBD FF00", BmsProtocolKind.Jbd, JbdServiceUuid, NotifyCharacteristicUuid, WriteCharacteristicUuid, null),
		new("TDT BMS UART FFF0", BmsProtocolKind.Tdt, Fff0ServiceUuid, Fff1CharacteristicUuid, Fff2CharacteristicUuid, FffaCharacteristicUuid),
		new("BMS UART FFF0 single characteristic", BmsProtocolKind.Jbd, Fff0ServiceUuid, Fff1CharacteristicUuid, Fff1CharacteristicUuid, null),
		new("HM-10 FFE0 single characteristic", BmsProtocolKind.Jbd, Ffe0ServiceUuid, Ffe1CharacteristicUuid, Ffe1CharacteristicUuid, null),
	];

	public const byte BasicInfoRegister = 0x03;
	public const byte CellVoltagesRegister = 0x04;
	public const byte MosControlRegister = 0xe1;

	public static Guid BleUuid(int shortId) => Guid.Parse($"0000{shortId:x4}-0000-1000-8000-00805f9b34fb");
}

internal enum BmsProtocolKind
{
	Jbd,
	Tdt
}

internal sealed record BmsProtocolDefinition(
	string Name,
	BmsProtocolKind Kind,
	Guid ServiceUuid,
	Guid NotifyCharacteristicUuid,
	Guid WriteCharacteristicUuid,
	Guid? ConfigCharacteristicUuid);
