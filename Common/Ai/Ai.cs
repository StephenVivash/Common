using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

using System.ComponentModel;

//using OpenAI;
//using OpenAI.Chat;
//using System.ClientModel;

//using System.Text.Json;

//using Gui.Controls;

namespace Common.Ai;

//#nullable enable

public class Ai
{
		public string[] _systemPrompts = [""];

		public List<string> _contextSessions = [];

		public List<List<string>> _userPrompts = [[], []];
		public List<List<string>> _chatReponses = [[], []];

		public ChatHistory _chatHistory = new();
		//public List<OpenAI.Chat.ChatMessage> _chatHistory1 = [];

		public void NewChat()
		{
			//foreach (var item in _chatHistory)
			//{
			//	if (item.Role != AuthorRole.System)
			//		_chatHistory.Remove(item);
			//}
		}

		public void AddSystemMessage(string message)
		{
			_chatHistory.AddSystemMessage(message);
		}

		public void AddUserMessage(string message)
		{
			_chatHistory.AddUserMessage(message);
		}

		public void AddAssistantMessage(string message)
		{
			_chatHistory.AddAssistantMessage(message);
		}
	/*

		public async Task<string> ChatOnceAsync(string aiService, string systemPrompt, string userPrompt, bool functions)
		{
			if (AiPreferences.IsNoneService(aiService))
				return string.Empty;

			var builder = Kernel.CreateBuilder();
			var serviceInfo = AiPreferences.AiService(aiService);
			var http = new HttpClient
			{
				Timeout = TimeSpan.FromMinutes(
				AiPreferences.IsOllamaService(aiService) ? 10 : 2)
			};

			builder.AddOpenAIChatCompletion(serviceInfo.ModelId,
				new Uri(serviceInfo.EndPoint),
				serviceInfo.Key, httpClient: http);

			if (functions)
				builder.Plugins.AddFromType<YijingPlugin>("Yijing");

			Kernel kernel = builder.Build();
			var chatService = kernel.GetRequiredService<IChatCompletionService>();

			var history = new ChatHistory();
			if (!string.IsNullOrWhiteSpace(systemPrompt))
				history.AddSystemMessage(systemPrompt);
			history.AddUserMessage(userPrompt);

			var settings = new OpenAIPromptExecutionSettings
			{
				Temperature = AiPreferences.AiTemperature,
				TopP = AiPreferences.AiTopP,
				MaxTokens = AiPreferences.AiMaxTokens,
			};

			if (functions)
				settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;

			var reply = await chatService.GetChatMessageContentAsync(history, settings, kernel);
			string response = reply.Content ?? string.Empty;

			response = response.Replace("**", ""); // Remove .md
			response = response.Replace("###", "");
			response = response.Replace("---", "");

			return response;
		}

		public async Task ChatAsync(string aiService, string prompt, bool functions)
		{
			try
			{
				if (AiPreferences.IsNoneService(aiService))
					return;

				var builder = Kernel.CreateBuilder();
				var serviceInfo = AiPreferences.AiService(aiService);
				var http = new HttpClient
				{
					Timeout = TimeSpan.FromMinutes(
					AiPreferences.IsOllamaService(aiService) ? 10 : 2)
				};

				builder.AddOpenAIChatCompletion(serviceInfo.ModelId,
					new Uri(serviceInfo.EndPoint),
					serviceInfo.Key, httpClient: http);

				//builder.AddOpenAITextToAudio(AiPreferences.AiModelId[aiService], AiPreferences.AiKey[aiService]);

				if (functions)
					builder.Plugins.AddFromType<YijingPlugin>("Yijing");
				Kernel kernel = builder.Build();

				var chatService = kernel.GetRequiredService<IChatCompletionService>();

				_chatHistory = new ChatHistory();
				foreach (var s1 in _systemPrompts)
					_chatHistory.AddSystemMessage(s1);

				for (int i = 0; i < 2; ++i)
					for (int j = 0; j < _userPrompts[i].Count(); ++j)
					{
						_chatHistory.AddUserMessage(_userPrompts[i][j]);
						_chatHistory.AddAssistantMessage(_chatReponses[i][j]);
					}

				_chatHistory.AddUserMessage(prompt);
				var settings = new OpenAIPromptExecutionSettings
				{
					Temperature = AiPreferences.AiTemperature,
					TopP = AiPreferences.AiTopP,
					MaxTokens = AiPreferences.AiMaxTokens,
				};

				if (functions)
					settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;

				var reply = await chatService.GetChatMessageContentAsync(_chatHistory, settings, kernel);
				string response = reply.Content;

				response = response?.Replace("**", ""); // Remove .md
				response = response?.Replace("###", "");
				response = response?.Replace("---", "");

				_userPrompts[1].Add(prompt);
				_chatReponses[1].Add(response!);
			}
			catch (Exception ex)
			{
				_userPrompts[1].Add(prompt);
				_chatReponses[1].Add(ex.Message +
					"\n\nEdit and save the AI preferences in the settings page.");
			}
		}
	}

	public class YijingPlugin
	{
		[KernelFunction("autocast_hexagram")]
		[Description("Navigate to and autocast on a DiagramView, wait and return the cast description")]

		public async Task<string> AutocastHexagram()
		{
			ViewDirectory.Invoke<SessionView>(async v => await v.NavigateToDialogPage());

			var tcs = new TaskCompletionSource<string>();
			ViewDirectory.Invoke<DiagramView>(async v =>
			{
				try
				{
					var result = await v.AutoCastHexagram();
					tcs.TrySetResult(result);
				}
				catch (Exception ex)
				{
					tcs.TrySetException(ex);
				}
			});
			return await tcs.Task.ConfigureAwait(false);

		}

		[KernelFunction("set_hexagram")]
		public void SetHexagram(int sequence) =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetHexagram(sequence));

		[KernelFunction("first_hexagram")]
		public void FirstHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetFirst());

		[KernelFunction("previous_hexagram")]
		public void PreviousHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetPrevious());

		[KernelFunction("next_hexagram")]
		public void NextHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetNext());

		[KernelFunction("last_hexagram")]
		public void LastHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetLast());

		[KernelFunction("move_hexagram")]
		public void MoveHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetMove());

		[KernelFunction("last_cast_hexagram")]
		public void LastCastHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetHome());

		[KernelFunction("inverse_hexagram")]
		public void InverseHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetInverse());

		[KernelFunction("opposite_hexagram")]
		public void OppositeHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetOpposite());

		[KernelFunction("transverse_hexagram")]
		public void TransverseHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetTransverse());

		[KernelFunction("nuclear_hexagram")]
		public void NuclearHexagram() =>
			ViewDirectory.Invoke<DiagramView>(v => v.SetNuclear());

		[KernelFunction("get_hexagram")]
		public int GetHexagram()
		{
			// Grab the latest DiagramView if it exists right now
			var v = ViewDirectory.Get<DiagramView>();
			if (v is null) return -1; // or throw / choose a sentinel that suits you

			// Read on the UI thread (safe even if GetHexagram touches UI-bound state)
			int value = -1;
			if (v.Dispatcher?.IsDispatchRequired == true)
				v.Dispatcher.Dispatch(() => value = v.GetHexagram());
			else
				value = v.GetHexagram();

			return value;
		}
	*/
}

















/*

	public async Task ChatAsync1(int aiService, string prompt)
	{
		try
		{
			OpenAIClientOptions openAIClientOptions = new()
			{
				NetworkTimeout = TimeSpan.FromSeconds(aiService == (int)eAiService.eOllama ? 600 : 120),
				Endpoint = new Uri(AiPreferences.AiEndPoint[aiService])
			};

			var requestOptions = new ChatCompletionOptions()
			{
				Temperature = AiPreferences.AiTemperature,
				TopP = AiPreferences.AiTopP,
				MaxOutputTokenCount = AiPreferences.AiMaxTokens,
				//AudioOptions = 
			};

			ApiKeyCredential credential = new(AiPreferences.AiKey[aiService]);
			var chatClient = new ChatClient(AiPreferences.AiModelId[aiService], credential, openAIClientOptions);

			_chatHistory1 = [];
			foreach (var s1 in _systemPrompts)
				_chatHistory1.Add(new SystemChatMessage(s1)); // UserChatMessage SystemChatMessage

			for (int i = 0; i < 2; ++i)
				for (int j = 0; j < _userPrompts[i].Count(); ++j)
				{
					_chatHistory1.Add(new UserChatMessage(_userPrompts[i][j]));
					_chatHistory1.Add(new AssistantChatMessage(_chatReponses[i][j]));
				}

			_chatHistory1.Add(new UserChatMessage(prompt));

			var completion = await chatClient.CompleteChatAsync(_chatHistory1, requestOptions);
			string response = completion.Value.Content[0].Text;

			response = response.Replace("**", ""); // Remove .md
			response = response.Replace("###", "");
			response = response.Replace("---", "");

			_userPrompts[1].Add(prompt);
			_chatReponses[1].Add(response);

			}
		catch (Exception ex)
		{
			_userPrompts[1].Add(prompt);
			_chatReponses[1].Add(ex.Message +
				"\n\nEdit the Documents\\Yijing\\appsettings.json file to correct the configuration and restart the application.");
		}
	}



	public async Task TestSkOpenAiTools(int aiService)
	{
		var builder = Kernel.CreateBuilder();
		builder.AddOpenAIChatCompletion(
			modelId: AiPreferences.AiModelId[aiService],
			apiKey: AiPreferences.AiKey[aiService]);

		builder.Plugins.AddFromType<ClockPlugin>("Utils");
		Kernel kernel = builder.Build();

		var chatService = kernel.GetRequiredService<IChatCompletionService>();
		var history = new ChatHistory();
		history.AddSystemMessage("You may call functions when needed.");
		history.AddUserMessage("What time is it in Sydney?");

		var settings = new OpenAIPromptExecutionSettings
		{
			ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
		};

		ApiKeyCredential credential = new(AiPreferences.AiKey[aiService]);
		var chatClient = new ChatClient(AiPreferences.AiModelId[aiService], credential, openAIClientOptions);

		var tools = new List<ChatTool> {
			ChatTool.CreateFunctionTool(
				"Utils_get_time",
				"Get the local time in a given city.",
				BinaryData.FromString("""
				{
				  "type": "object",
				  "properties": { "city": { "type": "string", "description": "City name" } },
				  "required": ["city"]
				}
				"""))
		};

		var messages = new List<ChatMessage> {
			new SystemChatMessage("You may call tools."),
			new UserChatMessage("What time is it in Sydney?")
		};

		var options = new ChatCompletionOptions
		{
			//Temperature = 0.7f,
			//TopP = 0.95f,
			//MaxOutputTokenCount = 1000,
			//AllowParallelToolCalls = true,
			//EndUserId = "Stephen",
			//ToolChoice = ChatToolChoice.CreateAutoChoice //.Auto
		};
		options.Tools.Add(tools[0]);

		while (true)
		{
			ChatCompletion completion = chatClient.CompleteChat(messages, options);
			messages.Add(new AssistantChatMessage(completion));

			bool handledToolCall = false;
			foreach (var call in completion.ToolCalls)
			{
				if (call is ChatToolCall.Function function && function.Name == "Utils_get_time")
				{
					using var doc = JsonDocument.Parse(function.Arguments);
					string city = doc.RootElement.GetProperty("city").GetString()!;
					var result1 = await kernel.InvokeAsync<string>("Utils", "get_time", new() { ["city"] = city });
					messages.Add(new ToolChatMessage(function.Id, result1));
					handledToolCall = true;
				}
			}
			if (!handledToolCall)
			{
				Console.WriteLine(completion.Content[0].Text);
				break;
			}
		}
	}
 
	public async void TestOpenAITools(int aiService)
	{

		OpenAIClientOptions openAIClientOptions = new()
		{
			NetworkTimeout = TimeSpan.FromSeconds(aiService == (int)eAiService.eOllama ? 600 : 120),
			Endpoint = new Uri(AiPreferences.AiEndPoint[aiService])
		};

		System.ClientModel.ApiKeyCredential credential = new(AiPreferences.AiKey[aiService]);
		var openAiChatClient = new ChatClient(AiPreferences.AiModelId[aiService], credential, openAIClientOptions);

		// Advertise a function tool
		var tools = new List<ChatTool> {
		ChatTool.CreateFunctionTool(
			"get_time", "Return ISO8601 time for an IANA timezone.",
			BinaryData.FromString(
			"""
			{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}
			"""))
		};

		var messages = new List<OpenAI.Chat.ChatMessage> {
			new SystemChatMessage("You may call tools."),
			new UserChatMessage("What's the time in Australia/Sydney?")
		};

		var opts = new ChatCompletionOptions();// { ToolChoice = ChatToolChoice.CreateNoneChoice };
		opts.Tools.Add(tools[0]);

		while (true)
		{
			ChatCompletion completion = await openAiChatClient.CompleteChatAsync(messages, opts);
			messages.Add(new AssistantChatMessage(completion));

			if (completion.ToolCalls.Count == 0)
			{
				Console.WriteLine(completion.Content[0].Text);
				break;
			}
			foreach (var tool in completion.ToolCalls.OfType<ChatToolCall.CreateFunctionToolCall>())
			{
				if (tool.Name == "get_time")
				{
					string tz = JsonDocument.Parse(tool.Arguments).RootElement.GetProperty("tz").GetString()!;
					var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tz)).ToString("O");
					messages.Add(new ToolChatMessage(tool.Id, now));
				}
			}
		}
	}

	public async void TestExtensionTools(int aiService)
	{
		OpenAIClientOptions openAIClientOptions = new()
		{
			NetworkTimeout = TimeSpan.FromSeconds(aiService == (int)eAiService.eOllama ? 600 : 120),
			Endpoint = new Uri(AiPreferences.AiEndPoint[aiService])
		};

		System.ClientModel.ApiKeyCredential credential = new(AiPreferences.AiKey[aiService]);
		var openAiChatClient = new ChatClient(AiPreferences.AiModelId[aiService], credential, openAIClientOptions);

		IChatClient chat = openAiChatClient.AsIChatClient();
		
		//IChatClient chat = new ChatClientBuilder(new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
		//	.GetChatClient("gpt-4o").AsIChatClient())
		//	.UseFunctionInvocation()
		//	.Build();

		var options = new ChatOptions
		{
			Tools = [AIFunctionFactory.Create((string location) => "15°C and drizzly", "get_current_weather", "Weather by location")]
		};

		var response = await chat.GetResponseAsync("Do I need a jacket in Sydney?", options);
		string x = response.Text;
	}

	public async Task ChatAsync(int aiService, string prompt)
	{
		string response = "";
		try
		{
			OllamaApiClient client = new OllamaApiClient(new Uri(AiPreferences.AiEndPoint[aiService]));
			Chat chat = new(client);
			chat.Model = AiPreferences.AiModelId[aiService];
			//chat.Options = options;
			chat.Messages.Add(new Message { Role = "user", Content = msg });

			var response1 = await chat.SendAsync("Hello") // .GetResponseAsync(options);

			// With the following code to properly consume the IAsyncEnumerable<string>:
			string str1 = "";
			await foreach (var chunk in chat.SendAsync("Hello"))
			{
				str1 += chunk;
			}

			string str2 = response1.Choices[0].Message.Content;

			client.ChatAsync("llama3.1:latest", new List<OllamaSharp.Message> 
				{ new OllamaSharp. Message { Role = "user", Content = msg } },
				options).Subscribe((response) =>
			{
				// Handle each response chunk as it arrives
				if (response != null && response.Choices != null && response.Choices.Count > 0)
				{
					var chunk = response.Choices[0].Message.Content;
					Console.WriteLine(chunk);
				}
			},
			() =>
			{
				// Handle completion of the stream
				Console.WriteLine("Stream completed.");
			});

			if (aiService == (int)eAiService.eOllama)
			{
				var ollamaClient = new OllamaApiClient(new Uri(AiPreferences.AiEndPoint[aiService]));

				ChatRequest request = new()
				{
					Model = AiPreferences.AiModelId[aiService],
					Messages = []
				};

				foreach (var s1 in _aiSystemPrompts)
					request.Messages.Add(new Message(ChatRole.System, s1));

				for (int i = 0; i < _aiUserPrompts[1].Count(); ++i)
				{
					request.Messages.Add(new Message(ChatRole.User, _aiUserPrompts[1][i]));
					request.Messages.Add(new Message(ChatRole.Assistant, _aiChatReponses[1][i]));
				}

				request.Messages.Add(new Message(ChatRole.User, msg));

				var completion = await ollamaClient.ChatAsync(request);
				string str = completion.Message.Content;
				str = str.Replace("**", "");
				str = str.Replace("###", "");
				str = str.Replace("---", "");

				_aiUserPrompts[1].Add(msg);
				_aiChatReponses[1].Add(str);
			}

            if (aiService == (int)eAiService.eOllama)
			{
				ChatOptions options = new()
				{
					Temperature = AiPreferences.AiTemperature,
					TopP = AiPreferences.AiTopP,
					MaxOutputTokens = AiPreferences.AiMaxTokens,

					//AllowParallelToolCalls = true,
					//EndUserId = "Stephen",
					//Functions = new List<ChatCompletionFunction> { ChatCompletionFunction.Chat },	
				};

				var ollamaChatClient = new OllamaChatClient(new Uri(AiPreferences.AiEndPoint[aiService]),
					AiPreferences.AiModelId[aiService]);

				List<Microsoft.Extensions.AI.ChatMessage> chatHistory = [];
				foreach (var s1 in _systemPrompts)
					chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, s1));
				for (int i = 0; i < _userPrompts[1].Count(); ++i)
				{
					chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, _userPrompts[1][i]));
					chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, _chatReponses[1][i]));
				}
				chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, prompt));

				var completion = await ollamaChatClient.GetResponseAsync(chatHistory, options);
                response = completion.Messages[0].Text;
			}
			else
			{
				OpenAIClientOptions openAIClientOptions = new()
				{
					NetworkTimeout = TimeSpan.FromSeconds(aiService == (int)eAiService.eOllama ? 360 : 120),
					Endpoint = new Uri(AiPreferences.AiEndPoint[aiService])
				};
				//if (aiService != (int)eAiService.eOpenAi)
				//openAIClientOptions.Endpoint = new Uri(AiPreferences.AiEndPoint[aiService]);

				var requestOptions = new ChatCompletionOptions()
				{
					Temperature = AiPreferences.AiTemperature,
					TopP = AiPreferences.AiTopP,
					MaxOutputTokenCount = AiPreferences.AiMaxTokens,
					//AudioOptions = 
				};

				System.ClientModel.ApiKeyCredential credential = new(AiPreferences.AiKey[aiService]);
				var openAiChatClient = new ChatClient(AiPreferences.AiModelId[aiService], credential, openAIClientOptions); // AppPreferences.OpenAiKey

				List<OpenAI.Chat.ChatMessage> chatHistory = [];
				foreach (var s1 in _systemPrompts)
					chatHistory.Add(new SystemChatMessage(s1)); // UserChatMessage SystemChatMessage

				for (int i = 0; i < 2; ++i)
					for (int j = 0; j < _userPrompts[i].Count(); ++j)
					{
						chatHistory.Add(new UserChatMessage(_userPrompts[i][j]));
						chatHistory.Add(new AssistantChatMessage(_chatReponses[i][j]));
					}

				chatHistory.Add(new UserChatMessage(prompt));

				var completion = await openAiChatClient.CompleteChatAsync(chatHistory, requestOptions);
				response = completion.Value.Content[0].Text;
			}

			response = response.Replace("**", ""); // Remove .md
			response = response.Replace("###", "");
			response = response.Replace("---", "");

			_userPrompts[1].Add(prompt);
			_chatReponses[1].Add(response);

		}
		catch (Exception ex)
		{
			_userPrompts[1].Add(prompt);
			_chatReponses[1].Add(ex.Message +
				"\n\nEdit the Documents\\Yijing\\appsettings.json file to correct the configuration and restart the application.");
		}
	}
}
*/
