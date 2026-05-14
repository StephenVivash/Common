using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

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
		public async Task<string> ChatOnceAsync(
			string aiService,
			string systemPrompt,
			string userPrompt,
			Action<IKernelBuilder>? configureKernel = null)
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

			configureKernel?.Invoke(builder);
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

			if (configureKernel is not null)
				settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;

			var reply = await chatService.GetChatMessageContentAsync(history, settings, kernel);
			string response = reply.Content ?? string.Empty;

			response = response.Replace("**", ""); // Remove .md
			response = response.Replace("###", "");
			response = response.Replace("---", "");

			return response;
		}

		public async Task ChatAsync(
			string aiService,
			string prompt,
			Action<IKernelBuilder>? configureKernel = null)
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

				configureKernel?.Invoke(builder);
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

				if (configureKernel is not null)
					settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;

				var reply = await chatService.GetChatMessageContentAsync(_chatHistory, settings, kernel);
				string response = reply.Content ?? string.Empty;

				response = response.Replace("**", ""); // Remove .md
				response = response.Replace("###", "");
				response = response.Replace("---", "");

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


