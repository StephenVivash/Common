namespace Common.Ai;

public interface IAiPreferenceStore
{
	string Get(string key, string defaultValue);
	float Get(string key, float defaultValue);
	int Get(string key, int defaultValue);
	void Set(string key, string value);
	void Set(string key, float value);
	void Set(string key, int value);
}

public static class AiPreferences
{
	public const string AiServiceNamesKey = "AI-ServiceNames";
	public const string AiServiceNone = "None";
	public const string AiTemperatureKey = "AI-Temperature";
	public const string AiTopPKey = "AI-TopP";
	public const string AiMaxTokensKey = "AI-MaxTokens";

	public const string AiServiceNamesDefault = "OpenAI,DeepSeek,Github,Ollama";
	public const float AiTemperatureDefault = 1.0f;
	public const float AiTopPDefault = 1.0f;
	public const int AiMaxTokensDefault = 10240;

	public const string OpenAiModelDefault = "gpt-5.2";
	public const string OpenAiEndPointDefault = "https://api.openai.com/v1";
	public const string OpenAiKeyDefault = "";
	public const string DeepSeekModelDefault = "deepseek-reasoner";
	public const string DeepSeekEndPointDefault = "https://api.deepseek.com";
	public const string DeepSeekKeyDefault = "";
	public const string GithubModelDefault = "grok-3";
	public const string GithubEndPointDefault = "https://models.inference.ai.azure.com";
	public const string GithubKeyDefault = "";
	public const string OllamaModelDefault = "gpt-oss:20b";
	public const string OllamaEndPointDefault = "http://localhost:11434/v1";
	public const string OllamaKeyDefault = "ollama";

	private static IAiPreferenceStore _preferenceStore = new MemoryAiPreferenceStore();

	public static IAiPreferenceStore PreferenceStore
	{
		get => _preferenceStore;
		set => _preferenceStore = value ?? new MemoryAiPreferenceStore();
	}

	public static void Load()
	{
		AiServiceNames = ParseServiceNames(PreferenceStore.Get(AiServiceNamesKey, AiServiceNamesDefault));
		if (AiServiceNames.Length == 0)
			AiServiceNames = ParseServiceNames(AiServiceNamesDefault);

		AiTemperature = PreferenceStore.Get(AiTemperatureKey, AiTemperatureDefault);
		AiTopP = PreferenceStore.Get(AiTopPKey, AiTopPDefault);
		AiMaxTokens = PreferenceStore.Get(AiMaxTokensKey, AiMaxTokensDefault);

		AiServices = new Dictionary<string, AiServiceInfo>(StringComparer.OrdinalIgnoreCase);
		foreach (var serviceName in AiServiceNames)
		{
			var defaults = GetServiceDefaults(serviceName);
			var modelId = PreferenceStore.Get(ServicePreferenceKey(serviceName, nameof(AiServiceInfo.ModelId)), defaults.ModelId);
			var endPoint = PreferenceStore.Get(ServicePreferenceKey(serviceName, nameof(AiServiceInfo.EndPoint)), defaults.EndPoint);
			var key = PreferenceStore.Get(ServicePreferenceKey(serviceName, nameof(AiServiceInfo.Key)), defaults.Key);
			AiServices[serviceName] = new AiServiceInfo(modelId, endPoint, key);
		}
	}

	public static void Save()
	{
		PreferenceStore.Set(AiServiceNamesKey, string.Join(",", AiServiceNames));
		PreferenceStore.Set(AiTemperatureKey, AiTemperature);
		PreferenceStore.Set(AiTopPKey, AiTopP);
		PreferenceStore.Set(AiMaxTokensKey, AiMaxTokens);

		foreach (var (serviceName, serviceInfo) in AiServices)
		{
			PreferenceStore.Set(ServicePreferenceKey(serviceName, nameof(AiServiceInfo.ModelId)), serviceInfo.ModelId);
			PreferenceStore.Set(ServicePreferenceKey(serviceName, nameof(AiServiceInfo.EndPoint)), serviceInfo.EndPoint);
			PreferenceStore.Set(ServicePreferenceKey(serviceName, nameof(AiServiceInfo.Key)), serviceInfo.Key);
		}
	}

	public static void Reset()
	{
		AiServiceNames = ParseServiceNames(AiServiceNamesDefault);
		AiTemperature = AiTemperatureDefault;
		AiTopP = AiTopPDefault;
		AiMaxTokens = AiMaxTokensDefault;
		AiServices = new Dictionary<string, AiServiceInfo>(StringComparer.OrdinalIgnoreCase);
		foreach (var serviceName in AiServiceNames)
			AiServices[serviceName] = GetServiceDefaults(serviceName);
		Save();
	}

	private static string ServicePreferenceKey(string serviceName, string fieldName)
	{
		return $"{serviceName}-{fieldName}";
	}

	private static string[] ParseServiceNames(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return [];

		return value
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Where(name => !name.Equals(AiServiceNone, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static AiServiceInfo GetServiceDefaults(string serviceName)
	{
		return serviceName switch
		{
			"OpenAI" => new AiServiceInfo(OpenAiModelDefault, OpenAiEndPointDefault, OpenAiKeyDefault),
			"DeepSeek" => new AiServiceInfo(DeepSeekModelDefault, DeepSeekEndPointDefault, DeepSeekKeyDefault),
			"Github" => new AiServiceInfo(GithubModelDefault, GithubEndPointDefault, GithubKeyDefault),
			"Ollama" => new AiServiceInfo(OllamaModelDefault, OllamaEndPointDefault, OllamaKeyDefault),
			_ => new AiServiceInfo("", "", "")
		};
	}

	public static AiServiceInfo AiService(string? aiService)
	{
		if (IsNoneService(aiService))
			return new AiServiceInfo("", "", "");

		if (AiServices.TryGetValue(aiService!, out var serviceInfo))
			return serviceInfo;

		return new AiServiceInfo("", "", "");
	}

	public static string NormalizeServiceName(string? serviceName)
	{
		if (string.IsNullOrWhiteSpace(serviceName))
			return AiServiceNone;

		if (serviceName.Equals(AiServiceNone, StringComparison.OrdinalIgnoreCase))
			return AiServiceNone;

		foreach (var name in AiServiceNames)
		{
			if (name.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
				return name;
		}

		return AiServiceNone;
	}

	public static string[] ServicePickerNames()
	{
		if (AiServiceNames.Length == 0)
			return [AiServiceNone];

		string[] names = new string[AiServiceNames.Length + 1];
		names[0] = AiServiceNone;
		Array.Copy(AiServiceNames, 0, names, 1, AiServiceNames.Length);
		return names;
	}

	public static bool IsNoneService(string? serviceName)
	{
		return string.IsNullOrWhiteSpace(serviceName)
			|| serviceName.Equals(AiServiceNone, StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsOllamaService(string? serviceName)
	{
		return serviceName?.Equals("Ollama", StringComparison.OrdinalIgnoreCase) == true;
	}

	public static float AiTemperature;
	public static float AiTopP;
	public static int AiMaxTokens;

	public static string[] AiServiceNames = [];
	public record AiServiceInfo(string ModelId, string EndPoint, string Key);

	public static Dictionary<string, AiServiceInfo> AiServices = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MemoryAiPreferenceStore : IAiPreferenceStore
{
	private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

	public string Get(string key, string defaultValue)
	{
		return _values.TryGetValue(key, out var value) && value is string typedValue ? typedValue : defaultValue;
	}

	public float Get(string key, float defaultValue)
	{
		return _values.TryGetValue(key, out var value) && value is float typedValue ? typedValue : defaultValue;
	}

	public int Get(string key, int defaultValue)
	{
		return _values.TryGetValue(key, out var value) && value is int typedValue ? typedValue : defaultValue;
	}

	public void Set(string key, string value)
	{
		_values[key] = value;
	}

	public void Set(string key, float value)
	{
		_values[key] = value;
	}

	public void Set(string key, int value)
	{
		_values[key] = value;
	}
}
