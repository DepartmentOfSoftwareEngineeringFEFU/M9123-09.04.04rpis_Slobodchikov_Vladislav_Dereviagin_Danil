using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Yuzu;
using Yuzu.Json;

namespace QualityAssurance.Utility;

public class TelegramClient
{
	private readonly TelegramClientOptions options;
	private readonly HttpClient httpClient;
	private readonly JsonSerializer serializer;
	private readonly JsonDeserializer deserializer;

	public CancellationToken GlobalCancelToken { get; }
	public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

	public event Action<string> OnApiResponseReceived;

	public TelegramClient(
		TelegramClientOptions options,
		HttpClient httpClient = null,
		CancellationToken cancellationToken = default
	) {
		this.options = options ?? throw new ArgumentNullException(nameof(options));
		this.httpClient = httpClient ?? new HttpClient();
		GlobalCancelToken = cancellationToken;

		var yuzuOptions = new CommonOptions {
			TagMode = TagMode.Aliases,
			AllowEmptyTypes = true,
			AllowUnknownFields = true,
			CheckForEmptyCollections = true,
		};
		var jsonOptions = new JsonSerializeOptions {
			ArrayLengthPrefix = false,
			SaveClass = JsonSaveClass.None,
			Unordered = true,
			MaxOnelineFields = 8,
			BOM = true,
			EnumAsString = true,
		};
		serializer = new JsonSerializer {
			JsonOptions = jsonOptions,
			Options = yuzuOptions,
		};
		deserializer = new JsonDeserializer {
			JsonOptions = jsonOptions,
			Options = yuzuOptions,
		};
	}

	public TelegramClient(
		string token,
		HttpClient httpClient = null,
		CancellationToken cancellationToken = default
	) : this(new TelegramClientOptions(token), httpClient, cancellationToken) { }

	public async Task<bool> TestApiAsync(CancellationToken cancellationToken = default)
	{
		try {
			await MakeRequestAsync(new GetMeRequest(), cancellationToken)
				.ConfigureAwait(continueOnCapturedContext: false);
			return true;
		} catch (ApiRequestException e) when (e.ErrorCode == 401) {
			return false;
		}
	}

	public virtual async Task<TResponse> MakeRequestAsync<TResponse>(
		IRequest<TResponse> request,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull(request, nameof(request));
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalCancelToken, cancellationToken);
		var url = $"{options.BaseRequestUrl}/{request.MethodName}";
		var httpRequest = new HttpRequestMessage(request.Method, url) {
			Content = request.ToHttpContent(serializer, deserializer),
		};
		using var httpResponse = await SendRequestAsync(httpClient, httpRequest, cts.Token)
			.ConfigureAwait(continueOnCapturedContext: false);
		if (OnApiResponseReceived is not null) {
			var message = await httpResponse.Content.ReadAsStringAsync(cts.Token)
				.ConfigureAwait(continueOnCapturedContext: false);
			OnApiResponseReceived.Invoke(message);
		}
		if (httpResponse.StatusCode != HttpStatusCode.OK) {
			var failedApiResponse = await httpResponse
				.DeserializeContentAsync<ApiResponse>(
					deserializer,
					response => response.ErrorCode == default || response.Description is null,
					cts.Token
				).ConfigureAwait(continueOnCapturedContext: false);
			throw ExceptionsParser.Parse(failedApiResponse);
		}
		var apiResponse = await httpResponse
			.DeserializeContentAsync<ApiResponse<TResponse>>(
				deserializer,
				response => !response.Ok || response.Result is null,
				cts.Token
			).ConfigureAwait(continueOnCapturedContext: false);
		return apiResponse.Result!;

		static async Task<HttpResponseMessage> SendRequestAsync(
			HttpClient httpClient,
			HttpRequestMessage httpRequest,
			CancellationToken cancellationToken
		) {
			HttpResponseMessage httpResponse;
			try {
				httpResponse = await httpClient
					.SendAsync(httpRequest, cancellationToken)
					.ConfigureAwait(continueOnCapturedContext: false);
			} catch (TaskCanceledException exception) {
				if (cancellationToken.IsCancellationRequested) {
					throw;
				}
				throw new RequestException("Request timed out", exception);
			} catch (Exception exception) {
				throw new RequestException("Exception during making request", exception);
			}
			return httpResponse;
		}
	}

	public async Task DownloadFileAsync(
		string filePath,
		Stream destination,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(filePath) || filePath.Length < 2) {
			throw new ArgumentException("Invalid file path", nameof(filePath));
		}
		ArgumentNullException.ThrowIfNull(destination, nameof(destination));

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalCancelToken, cancellationToken);
		var fileUri = $"{options.BaseFileUrl}/{filePath}";
		using var httpResponse = await GetResponseAsync(httpClient, fileUri, cts.Token)
			.ConfigureAwait(continueOnCapturedContext: false);

		if (!httpResponse.IsSuccessStatusCode) {
			var failedApiResponse = await httpResponse
				.DeserializeContentAsync<ApiResponse>(
					deserializer,
					response => response.ErrorCode == default || response.Description is null,
					cts.Token
				).ConfigureAwait(continueOnCapturedContext: false);
			throw ExceptionsParser.Parse(failedApiResponse);
		}
		if (httpResponse.Content is null) {
			throw new RequestException("Response doesn't contain any content", httpResponse.StatusCode);
		}
		try {
			await httpResponse.Content.CopyToAsync(destination, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
		} catch (Exception exception) {
			throw new RequestException("Exception during file download", httpResponse.StatusCode, exception);
		}

		static async Task<HttpResponseMessage> GetResponseAsync(
			HttpClient httpClient,
			string fileUri,
			CancellationToken cancellationToken
		) {
			HttpResponseMessage httpResponse;
			try {
				httpResponse = await httpClient
					.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
					.ConfigureAwait(continueOnCapturedContext: false);
			} catch (TaskCanceledException exception) {
				if (cancellationToken.IsCancellationRequested) {
					throw;
				}
				throw new RequestException("Request timed out", exception);
			} catch (Exception exception) {
				throw new RequestException("Exception during file download", exception);
			}
			return httpResponse;
		}
	}

	public async Task<Message> SendTextMessageAsync(
		ChatId chatId,
		string text,
		int? messageThreadId = default,
		string parseMode = default,
		bool disableNotification = default,
		bool protectContent = default,
		CancellationToken cancellationToken = default
	) {
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalCancelToken, cancellationToken);
		return await MakeRequestAsync(
			new SendMessageRequest {
				ChatId = chatId,
				Text = text,
				MessageThreadId = messageThreadId,
				ParseMode = parseMode,
				DisableNotification = disableNotification,
				ProtectContent = protectContent,
			},
			cts.Token
		).ConfigureAwait(continueOnCapturedContext: false);
	}

	public async Task<Message> SendDocumentAsync(
		ChatId chatId,
		InputFile document,
		int? messageThreadId = default,
		string caption = default,
		string parseMode = default,
		bool disableContentTypeDetection = default,
		bool disableNotification = default,
		bool protectContent = default,
		CancellationToken cancellationToken = default
	) {
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalCancelToken, cancellationToken);
		return await MakeRequestAsync(
			new SendDocumentRequest {
				ChatId = chatId,
				Document = document,
				MessageThreadId = messageThreadId,
				Caption = caption,
				ParseMode = parseMode,
				DisableContentTypeDetection = disableContentTypeDetection,
				DisableNotification = disableNotification,
				ProtectContent = protectContent,
			},
			cts.Token
		).ConfigureAwait(continueOnCapturedContext: false);
	}

	public async Task<bool> DeleteMessageAsync(
		ChatId chatId,
		int messageId,
		CancellationToken cancellationToken = default
	) {
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalCancelToken, cancellationToken);
		return await MakeRequestAsync(
			new DeleteMessageRequest {
				ChatId = chatId,
				MessageId = messageId,
			},
			cts.Token
		).ConfigureAwait(continueOnCapturedContext: false);
	}

	public async Task<bool> DeleteMessagesAsync(
		ChatId chatId,
		List<int> messageIds,
		CancellationToken cancellationToken = default
	) {
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalCancelToken, cancellationToken);
		return await MakeRequestAsync(
			new DeleteMessagesRequest {
				ChatId = chatId,
				MessageIds = messageIds,
			},
			cts.Token
		).ConfigureAwait(continueOnCapturedContext: false);
	}
}

public class TelegramClientOptions
{
	private const string BaseTelegramUrl = "https://api.telegram.org";

	public string Token { get; }
	public string BaseUrl { get; }
	public bool UseTestEnvironment { get; }
	public long BotId { get; }
	public bool LocalBotServer { get; }
	public string BaseFileUrl { get; }
	public string BaseRequestUrl { get; }

	public TelegramClientOptions(string token, string baseUrl = default, bool useTestEnvironment = false)
	{
		Token = token ?? throw new ArgumentNullException(nameof(token));
		BaseUrl = baseUrl;
		UseTestEnvironment = useTestEnvironment;

		var index = token.IndexOf(':', StringComparison.Ordinal);
		if (index is < 1 or > 16) {
			throw new ArgumentException("Bot token invalid", nameof(token));
		}
		var botIdSpan = token[..index];
		if (!long.TryParse(botIdSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var botId)) {
			throw new ArgumentException("Bot token invalid", nameof(token));
		}
		BotId = botId;
		LocalBotServer = baseUrl is not null;
		var effectiveBaseUrl = LocalBotServer
			? ExtractBaseUrl(baseUrl)
			: BaseTelegramUrl;
		BaseRequestUrl = useTestEnvironment
			? $"{effectiveBaseUrl}/bot{token}/test"
			: $"{effectiveBaseUrl}/bot{token}";
		BaseFileUrl = useTestEnvironment
			? $"{effectiveBaseUrl}/file/bot{token}/test"
			: $"{effectiveBaseUrl}/file/bot{token}";
	}

	private static string ExtractBaseUrl(string baseUrl)
	{
		ArgumentNullException.ThrowIfNull(baseUrl, nameof(baseUrl));
		if (
			!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
			|| string.IsNullOrEmpty(baseUri.Scheme)
			|| string.IsNullOrEmpty(baseUri.Authority)
		) {
			throw new ArgumentException(
				"""Invalid format. A valid base URL should look like "http://localhost:8081" """,
				nameof(baseUrl)
			);
		}
		return $"{baseUri.Scheme}://{baseUri.Authority}";
	}
}

public class ApiResponse
{
	[YuzuMember("description")]
	public string Description { get; init; }

	[YuzuMember("error_code")]
	public int ErrorCode { get; init; }

	[YuzuMember("parameters")]
	public ResponseParameters Parameters { get; init; }
}

public class ApiResponse<TResult>
{
	[YuzuMember("ok")]
	public bool Ok { get; init; }

	[YuzuMember("description")]
	public string Description { get; init; }

	[YuzuMember("error_code")]
	public int ErrorCode { get; init; }

	[YuzuMember("parameters")]
	public ResponseParameters Parameters { get; init; }

	[YuzuMember("result")]
	public TResult Result { get; init; }
}

public partial class ResponseParameters
{
	[YuzuMember("migrate_to_chat_id")]
	public long? MigrateToChatId { get; set; }

	[YuzuMember("retry_after")]
	public int? RetryAfter { get; set; }
}

public interface IExceptionParser
{
	ApiRequestException Parse(ApiResponse apiResponse);
}

public class DefaultExceptionParser : IExceptionParser
{
	public ApiRequestException Parse(ApiResponse apiResponse)
	{
		ArgumentNullException.ThrowIfNull(apiResponse, nameof(apiResponse));
		return new (apiResponse.Description, apiResponse.ErrorCode, apiResponse.Parameters);
	}
}

public class RequestException : Exception
{
	public HttpStatusCode? HttpStatusCode { get; }

	public RequestException(string message) : base(message) { }

	public RequestException(string message, Exception innerException) : base(message, innerException) { }

	public RequestException(string message, HttpStatusCode httpStatusCode) : base(message)
	{
		HttpStatusCode = httpStatusCode;
	}

	public RequestException(string message, HttpStatusCode httpStatusCode, Exception innerException) : base(message, innerException)
	{
		HttpStatusCode = httpStatusCode;
	}
}

public class ApiRequestException : RequestException
{
	public virtual int ErrorCode { get; }
	public ResponseParameters Parameters { get; }

	public ApiRequestException(string message) : base(message) { }

	public ApiRequestException(string message, int errorCode) : base(message)
	{
		ErrorCode = errorCode;
	}

	public ApiRequestException(string message, Exception innerException) : base(message, innerException) { }

	public ApiRequestException(string message, int errorCode, Exception innerException) : base(message, innerException)
	{
		ErrorCode = errorCode;
	}

	public ApiRequestException(string message, int errorCode, ResponseParameters parameters) : base(message)
	{
		ErrorCode = errorCode;
		Parameters = parameters;
	}

	public ApiRequestException(
		string message,
		int errorCode,
		ResponseParameters parameters,
		Exception innerException
	) : base(message, innerException)
	{
		ErrorCode = errorCode;
		Parameters = parameters;
	}

	public override string ToString()
	{
		var str = base.ToString();
		if (str.IndexOf(':', StringComparison.Ordinal) is >= 0 and int colon) {
			str = $"Telegram Bot API error {ErrorCode}{str[colon..]}";
		}
		return str;
	}
}

internal static class HttpResponseMessageExtensions
{
	internal static async Task<T> DeserializeContentAsync<T>(
		this HttpResponseMessage httpResponse,
		JsonDeserializer deserializer,
		Func<T, bool> guard,
		CancellationToken cancellationToken = default)
		where T : class
	{
		Stream contentStream = null;
		if (httpResponse.Content is null) {
			throw new RequestException("Response doesn't contain any content", httpResponse.StatusCode);
		}
		try {
			T deserializedObject;
			try {
				contentStream = await httpResponse.Content
					.ReadAsStreamAsync(cancellationToken)
					.ConfigureAwait(continueOnCapturedContext: false);
				deserializedObject = deserializer.FromStream<T>(contentStream);
			} catch (Exception exception) {
				throw CreateRequestException(httpResponse, "There was an exception during deserialization of the response", exception);
			}
			if (deserializedObject is null) {
				throw CreateRequestException(httpResponse, "Required properties not found in response");
			}
			if (guard(deserializedObject)) {
				throw CreateRequestException(httpResponse, "Required properties not found in response");
			}
			return deserializedObject;
		} finally {
			if (contentStream is not null) {
				await contentStream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private static RequestException CreateRequestException(
		HttpResponseMessage httpResponse,
		string message,
		Exception exception = default
	) {
		return exception is null
			? new (message, httpResponse.StatusCode)
			: new (message, httpResponse.StatusCode, exception);
	}
}

public interface IRequest
{
	HttpMethod Method { get; }
	string MethodName { get; }
	bool IsWebhookResponse { get; set; }
	HttpContent ToHttpContent(JsonSerializer serializer, JsonDeserializer deserializer);
}

public interface IRequest<TResponse> : IRequest { }

public abstract class RequestBase<TResponse> : IRequest<TResponse>
{
	public HttpMethod Method { get; }
	public string MethodName { get; }
	public bool IsWebhookResponse { get; set; }

	[YuzuMember("method")]
	public string WebHookMethodName => IsWebhookResponse ? MethodName : default;

	protected RequestBase(string methodName) : this(methodName, HttpMethod.Post) { }

	protected RequestBase(string methodName, HttpMethod method)
	{
		(MethodName, Method) = (methodName, method);
	}

	public virtual HttpContent ToHttpContent(JsonSerializer serializer, JsonDeserializer deserializer)
	{
		return new StringContent(serializer.ToString(this), Encoding.UTF8, "application/json");
	}
}

public abstract class ParameterlessRequest<TResult> : RequestBase<TResult>
{
	protected ParameterlessRequest(string methodName) : base(methodName) { }

	protected ParameterlessRequest(string methodName, HttpMethod method) : base(methodName, method) { }

	public override HttpContent ToHttpContent(JsonSerializer serializer, JsonDeserializer deserializer)
	{
		return IsWebhookResponse ? base.ToHttpContent(serializer, deserializer) : default;
	}
}

public partial class GetMeRequest : ParameterlessRequest<User>
{
	public GetMeRequest() : base("getMe") { }
}

public partial class User
{
	[YuzuMember("id")]
	public long Id { get; set; }

	[YuzuMember("is_bot")]
	public bool IsBot { get; set; }

	[YuzuMember("first_name")]
	public string FirstName { get; set; }

	[YuzuMember("last_name")]
	public string LastName { get; set; }

	[YuzuMember("username")]
	public string Username { get; set; }

	[YuzuMember("language_code")]
	public string LanguageCode { get; set; }

	[YuzuMember("is_premium")]
	public bool IsPremium { get; set; }

	[YuzuMember("added_to_attachment_menu")]
	public bool AddedToAttachmentMenu { get; set; }

	[YuzuMember("can_join_groups")]
	public bool CanJoinGroups { get; set; }

	[YuzuMember("can_read_all_group_messages")]
	public bool CanReadAllGroupMessages { get; set; }

	[YuzuMember("supports_inline_queries")]
	public bool SupportsInlineQueries { get; set; }

	[YuzuMember("can_connect_to_business")]
	public bool CanConnectToBusiness { get; set; }
}

public partial class Message
{
	[YuzuMember("message_id")]
	public int MessageId { get; set; }

	[YuzuMember("message_thread_id")]
	public int? MessageThreadId { get; set; }

	[YuzuMember("from")]
	public User From { get; set; }

	public DateTime Date { get; set; }

	[YuzuMember("date")]
	public long InternalDate
	{
		get => ((DateTimeOffset)Date.ToUniversalTime()).ToUnixTimeSeconds();
		set => Date = DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
	}

	[YuzuMember("text")]
	public string Text { get; set; }
}

public partial class SendMessageRequest : RequestBase<Message>
{
	public ChatId ChatId { get; set; }

	[YuzuMember("chat_id")]
	public string InternalChatId
	{
		get => ChatId?.ToString();
		set => ChatId = new ChatId(value);
	}

	[YuzuMember("text")]
	public string Text { get; set; }

	[YuzuMember("message_thread_id")]
	public int? MessageThreadId { get; set; }

	[YuzuMember("parse_mode")]
	public string ParseMode { get; set; }

	[YuzuRequired("disable_notification")]
	public bool DisableNotification { get; set; }

	[YuzuMember("protect_content")]
	public bool ProtectContent { get; set; }

	public SendMessageRequest() : base("sendMessage") { }
}

public partial class DeleteMessageRequest : RequestBase<bool>
{
	public ChatId ChatId { get; set; }

	[YuzuMember("chat_id")]
	public string InternalChatId
	{
		get => ChatId?.ToString();
		set => ChatId = new ChatId(value);
	}

	[YuzuMember("message_id")]
	public int MessageId { get; set; }

	public DeleteMessageRequest() : base("deleteMessage") { }
}

public partial class DeleteMessagesRequest : RequestBase<bool>
{
	public ChatId ChatId { get; set; }

	[YuzuMember("chat_id")]
	public string InternalChatId
	{
		get => ChatId?.ToString();
		set => ChatId = new ChatId(value);
	}

	[YuzuMember("message_ids")]
	public List<int> MessageIds { get; set; }

	public DeleteMessagesRequest() : base("deleteMessages") { }
}

public class ChatId : IEquatable<ChatId>
{
	[YuzuMember("identifier")]
	public long? Identifier { get; set; }

	[YuzuMember("username")]
	public string Username { get; set; }

	public ChatId() { }

	public ChatId(long identifier)
	{
		Identifier = identifier;
	}

	public ChatId(string username)
	{
		ArgumentNullException.ThrowIfNull(username, nameof(username));
		if (username.Length > 1 && username[0] == '@') {
			Username = username;
		} else if (
			long.TryParse(
				username,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out var identifier
			)
		) {
			Identifier = identifier;
		} else {
			throw new ArgumentException("Username value should be Identifier or Username that starts with @", nameof(username));
		}
	}

	public override bool Equals(object obj) =>
		obj switch {
			ChatId chatId => this == chatId,
			_ => false,
		};

	public bool Equals(ChatId other) => this == other;

	public override int GetHashCode() => StringComparer.InvariantCulture.GetHashCode(ToString());

	public override string ToString() => (Username ?? Identifier?.ToString(CultureInfo.InvariantCulture))!;

	public static implicit operator ChatId(long identifier) => new (identifier);

	public static implicit operator ChatId(string username) => new (username);

	public static bool operator ==(ChatId lhs, ChatId rhs)
	{
		if (lhs is null || rhs is null) {
			return false;
		}
		if (lhs.Identifier is not null && rhs.Identifier is not null) {
			return lhs.Identifier == rhs.Identifier;
		}
		if (lhs.Username is not null && rhs.Username is not null) {
			return string.Equals(lhs.Username, rhs.Username, StringComparison.Ordinal);
		}
		return false;
	}

	public static bool operator !=(ChatId obj1, ChatId obj2) => !(obj1 == obj2);
}

public abstract class FileRequestBase<TResponse> : RequestBase<TResponse>
{
	protected FileRequestBase(string methodName) : base(methodName) { }

	protected FileRequestBase(string methodName, HttpMethod method) : base(methodName, method) { }

	protected MultipartFormDataContent GenerateMultipartFormDataContent(JsonSerializer serializer, JsonDeserializer deserializer)
	{
		var json = serializer.ToString(this);
		var jsonDictionary = (Dictionary<string, object>)deserializer.FromString(json);
		var boundary = $"{Guid.NewGuid()}{DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}";
		var multipartContent = new MultipartFormDataContent(boundary);
		foreach (var (key, value) in jsonDictionary) {
			var valueString = serializer.ToString(value).Trim('"');
			var valueUnescaped = Regex.Unescape(valueString);
			var stringContent = new StringContent(valueUnescaped);
			multipartContent.Add(stringContent, key);
		}
		return multipartContent;
	}
}

public partial class SendDocumentRequest : FileRequestBase<Message>
{
	public ChatId ChatId { get; set; }

	[YuzuMember("chat_id")]
	public string InternalChatId
	{
		get => ChatId?.ToString();
		set => ChatId = new ChatId(value);
	}

	public InputFile Document { get; set; }

	[YuzuMember("message_thread_id")]
	public int? MessageThreadId { get; set; }

	[YuzuMember("caption")]
	public string Caption { get; set; }

	[YuzuMember("parse_mode")]
	public string ParseMode { get; set; }

	[YuzuMember("disable_content_type_detection")]
	public bool DisableContentTypeDetection { get; set; }

	[YuzuRequired("disable_notification")]
	public bool DisableNotification { get; set; }

	[YuzuMember("protect_content")]
	public bool ProtectContent { get; set; }

	public SendDocumentRequest() : base("sendDocument") { }

	public override HttpContent ToHttpContent(JsonSerializer serializer, JsonDeserializer deserializer)
	{
		return Document is InputFileStream
			? GenerateMultipartFormDataContent(serializer, deserializer).AddContentIfInputFile(Document, "document")
			: base.ToHttpContent(serializer, deserializer);
	}
}

public abstract class InputFile
{
	public static InputFileStream FromStream(Stream stream, string fileName = default) => new (stream, fileName);

	public static implicit operator InputFile(Stream stream) => FromStream(stream);
}

public class InputFileStream : InputFile
{
	public Stream Content { get; set; }

	public string FileName { get; }

	public InputFileStream(Stream content, string fileName = default)
	{
		(Content, FileName) = (content, fileName);
	}

	public InputFileStream() { }

	public static implicit operator InputFileStream(Stream stream) => new (stream);
}

public static class HttpContentExtensions
{
	internal static MultipartFormDataContent AddContentIfInputFile(
		this MultipartFormDataContent multipartContent,
		InputFile media,
		string name
	) {
		if (media is not InputFileStream inputFile) {
			return multipartContent;
		}
		var fileName = inputFile.FileName ?? name;
		var contentDisposition = $@"form-data; name=""{name}""; filename=""{fileName}""".EncodeUtf8();
		var mediaPartContent = new StreamContent(inputFile.Content) {
			Headers = {
				{ "Content-Type", "application/octet-stream" },
				{ "Content-Disposition", contentDisposition },
			},
		};
		multipartContent.Add(mediaPartContent, name, fileName);
		return multipartContent;
	}
}

public static class StringExtensions
{
	public static string EncodeUtf8(this string value)
	{
		return new (Encoding.UTF8.GetBytes(value).Select(Convert.ToChar).ToArray());
	}
}
