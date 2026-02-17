using System;
using System.Collections.Generic;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using Mindscape.Raygun4Net;
#if NETFRAMEWORK
using Mindscape.Raygun4Net.Messages;
#else
using Mindscape.Raygun4Net.AspNetCore;
#endif

namespace NLog.Raygun
{
  [Target("Raygun")]
  public class RaygunTarget : TargetWithContext
  {
    [RequiredParameter]
    public string ApiKey
    {
      get { return (_apiKey as SimpleLayout)?.Text ?? _apiKey?.ToString(); }
      set { _apiKey = value; }
    }
    private Layout _apiKey;

    public string Tags { get; set; }

    /// <summary>
    /// Adds a list of keys to ignore when attaching the Form data of an HTTP POST request. This allows
    /// you to remove sensitive data from the transmitted copy of the Form on the HttpRequest by specifying the keys you want removed.
    /// This method is only effective in a web context.
    /// </summary>
    public string IgnoreFormFieldNames { get; set; }

    /// <summary>
    /// Adds a list of keys to ignore when attaching the cookies of an HTTP POST request. This allows
    /// you to remove sensitive data from the transmitted copy of the Cookies on the HttpRequest by specifying the keys you want removed.
    /// This method is only effective in a web context.
    /// </summary>
    public string IgnoreCookieNames { get; set; }

    /// <summary>
    /// Adds a list of keys to ignore when attaching the server variables of an HTTP POST request. This allows
    /// you to remove sensitive data from the transmitted copy of the ServerVariables on the HttpRequest by specifying the keys you want removed.
    /// This method is only effective in a web context.
    /// </summary>
    public string IgnoreServerVariableNames { get; set; }

    /// <summary>
    /// Adds a list of keys to ignore when attaching the headers of an HTTP POST request. This allows
    /// you to remove sensitive data from the transmitted copy of the Headers on the HttpRequest by specifying the keys you want removed.
    /// This method is only effective in a web context.
    /// </summary>
    public string IgnoreHeaderNames { get; set; }

    /// <summary>
    /// Adds a list of keys to remove from the <see cref="RaygunRequestMessage.QueryString" /> property of the <see cref="RaygunRequestMessage" />
    /// </summary>
    public string IgnoreQueryParameterNames { get; set; }

    /// <summary>
    /// Adds a list of keys to remove from the following sections of the <see cref="RaygunRequestMessage" />
    /// <see cref="RaygunRequestMessage.Headers" />
    /// <see cref="RaygunRequestMessage.QueryString" />
    /// <see cref="RaygunRequestMessage.Cookies" />
    /// <see cref="RaygunRequestMessage.Data" />
    /// <see cref="RaygunRequestMessage.Form" />
    /// <see cref="RaygunRequestMessage.RawData" />
    /// </summary>
    public string IgnoreSensitiveFieldNames { get; set; }

    /// <summary>
    /// Specifies whether or not RawData from web requests is ignored when sending reports to Raygun.io.
    /// The default is false which means RawData will be sent to Raygun.io.
    /// </summary>
    public bool IsRawDataIgnored { get { return _isRawDataIgnored ?? false; } set { _isRawDataIgnored = value; } }
    private bool? _isRawDataIgnored;

    /// <summary>
    /// Convert informational LogEvents without Exceptions into Breadcrumbs
    /// </summary>
    public bool IncludeBreadcrumbMessages { get; set; }

    /// <summary>
    /// Explicitly defines lookup of user-identity for Raygun events.
    /// </summary>
    public Layout UserIdentityInfo { get; set; }

    /// <summary>
    /// Attempt to get the entry assembly version
    /// </summary>
    public bool UseExecutingAssemblyVersion { get; set; }

    /// <summary>
    /// Explicitly defines an application version for Raygun events.
    /// NOTE: This value will be ignored if UseExecutingAssemblyVersion is set to true and returns a value.
    /// </summary>
    public string ApplicationVersion
    {
      get { return (_applicationVersion as SimpleLayout)?.Text ?? _applicationVersion?.ToString(); }
      set { _applicationVersion = value; }
    }
    private Layout _applicationVersion;

    private RaygunClient _raygunClient;

    public RaygunTarget()
    {
      IncludeEventProperties = true;
      Layout = "${message}";
      _applicationVersion = "${assembly-version:cached=true:type=File}";
    }

    protected override void InitializeTarget()
    {
      if (ContextProperties.Count == 0)
      {
        ContextProperties.Add(new TargetPropertyWithContext("RenderedLogMessage", Layout));
        ContextProperties.Add(new TargetPropertyWithContext("LogMessageTemplate", "${message:raw=true}"));
      }

      base.InitializeTarget();
    }

    protected override void CloseTarget()
    {
      base.CloseTarget();
      _raygunClient = null;
    }

    protected override void Write(AsyncLogEventInfo eventInfo)
    {
      try
      {
        _raygunClient = _raygunClient ?? (_raygunClient = CreateRaygunClient());

        var exception = ExtractException(eventInfo.LogEvent);

        var contextProperties = GetAllProperties(eventInfo.LogEvent);

        contextProperties.Remove("Tags");
        contextProperties.Remove("tags");

        var userCustomData = new UserCustomDictionary(contextProperties);

        if (exception == null)
        {
          var layoutLogMessage = RenderLogEvent(Layout, eventInfo.LogEvent);

          if (IncludeBreadcrumbMessages && eventInfo.LogEvent.Level < LogLevel.Error)
          {
            RecordBreadcrumb(eventInfo.LogEvent, layoutLogMessage, userCustomData);
            return;
          }

          exception = new RaygunException(layoutLogMessage);
        }

        var tags = ExtractTags(eventInfo.LogEvent, exception);
        var userIdentityInfo = RenderLogEvent(UserIdentityInfo, eventInfo.LogEvent);

#if NETFRAMEWORK
        var userIdentity = string.IsNullOrEmpty(userIdentityInfo) ? null : new Mindscape.Raygun4Net.Messages.RaygunIdentifierMessage(userIdentityInfo);
        _raygunClient.SendInBackground(exception, tags, userCustomData, userIdentity);

        eventInfo.Continuation(null);
#else
        var userIdentity = string.IsNullOrEmpty(userIdentityInfo) ? null : new RaygunIdentifierMessage(userIdentityInfo);
        _raygunClient.SendInBackground(exception, tags, userCustomData, userIdentity).ContinueWith((t, s) => SendCompleted(t.Exception, (AsyncContinuation)s), eventInfo.Continuation);
#endif
      }
      catch (Exception ex)
      {
        InternalLogger.Error(ex, "Raygun(Name={0}): Failed to send logevent.", Name);
        eventInfo.Continuation(ex);
      }
    }

    private RaygunClient CreateRaygunClient()
    {
      RaygunClient client = null;

      var apiKey = _apiKey?.Render(LogEventInfo.CreateNullEvent()) ?? string.Empty;
#if NETFRAMEWORK
      client = string.IsNullOrEmpty(apiKey) ? new RaygunClient() : new RaygunClient(apiKey);

      if (UseExecutingAssemblyVersion)
      {
        client.ApplicationVersion = GetExecutingAssemblyVersion();
      }

      var applicationVersion = client.ApplicationVersion;
      if (string.IsNullOrEmpty(applicationVersion))
      {
        if (_applicationVersion != null)
        {
          client.ApplicationVersion = _applicationVersion.Render(LogEventInfo.CreateNullEvent());
        }
        else
        {
          client.ApplicationVersion = GetExecutingAssemblyVersion();
        }
      }

      if (IgnoreSensitiveFieldNames != null)
      {
        client.IgnoreSensitiveFieldNames(SplitValues(IgnoreSensitiveFieldNames));
      }

      if (IgnoreFormFieldNames != null)
      {
        client.IgnoreFormFieldNames(SplitValues(IgnoreFormFieldNames));
      }

      if (IgnoreCookieNames != null)
      {
        client.IgnoreCookieNames(SplitValues(IgnoreCookieNames));
      }

      if (IgnoreHeaderNames != null)
      {
        client.IgnoreHeaderNames(SplitValues(IgnoreHeaderNames));
      }

      if (IgnoreServerVariableNames != null)
      {
        client.IgnoreServerVariableNames(SplitValues(IgnoreServerVariableNames));
      }

      if (IgnoreQueryParameterNames != null)
      {
        client.IgnoreQueryParameterNames(SplitValues(IgnoreQueryParameterNames));
      }

      if (_isRawDataIgnored.HasValue)
      {
        client.IsRawDataIgnored = _isRawDataIgnored.Value;
      }
#else
      var settings = new RaygunSettings();
      if (!string.IsNullOrEmpty(apiKey))
      {
        settings.ApiKey = apiKey;
      }
      if (UseExecutingAssemblyVersion)
      {
        settings.ApplicationVersion = GetExecutingAssemblyVersion();
      }

      var applicationVersion = settings.ApplicationVersion;
      if (string.IsNullOrEmpty(applicationVersion))
      {
        if (_applicationVersion != null)
        {
          settings.ApplicationVersion = _applicationVersion.Render(LogEventInfo.CreateNullEvent());
        }
        else
        {
          settings.ApplicationVersion = GetExecutingAssemblyVersion();
        }
      }

      if (IgnoreSensitiveFieldNames != null)
      {
        settings.IgnoreSensitiveFieldNames.AddRange(SplitValues(IgnoreSensitiveFieldNames));
      }

      if (IgnoreFormFieldNames != null)
      {
        settings.IgnoreFormFieldNames.AddRange(SplitValues(IgnoreFormFieldNames));
      }

      if (IgnoreCookieNames != null)
      {
        settings.IgnoreCookieNames.AddRange(SplitValues(IgnoreCookieNames));
      }

      if (IgnoreHeaderNames != null)
      {
        settings.IgnoreHeaderNames.AddRange(SplitValues(IgnoreHeaderNames));
      }

      if (IgnoreServerVariableNames != null)
      {
        settings.IgnoreServerVariableNames.AddRange(SplitValues(IgnoreServerVariableNames));
      }

      if (IgnoreQueryParameterNames != null)
      {
        settings.IgnoreQueryParameterNames.AddRange(SplitValues(IgnoreQueryParameterNames));
      }

      if (_isRawDataIgnored.HasValue)
      {
        settings.IsRawDataIgnored = _isRawDataIgnored.Value;
      }
     
      client = new RaygunClient(settings);
#endif
      return client;
    }

    private static Exception ExtractException(LogEventInfo logEvent)
    {
      if (logEvent.Exception != null)
      {
        return logEvent.Exception;
      }

      if (logEvent.Parameters != null && logEvent.Parameters.Length > 0)
      {
        return logEvent.Parameters[0] as Exception;
      }

      return null;
    }

    private void RecordBreadcrumb(LogEventInfo logEvent, string layoutMessage, IDictionary<string, object> userCustomData)
    {
      var crumb = new RaygunBreadcrumb
      {
        Message = layoutMessage,
        Category = logEvent.LoggerName,
      };

#if NETFRAMEWORK
      if (logEvent.Level == LogLevel.Warn)
      {
        crumb.Level = Mindscape.Raygun4Net.Breadcrumbs.RaygunBreadcrumbLevel.Warning;
      }
      else if (logEvent.Level == LogLevel.Error || logEvent.Level == LogLevel.Fatal)
      {
        crumb.Level = Mindscape.Raygun4Net.Breadcrumbs.RaygunBreadcrumbLevel.Error;
      }
#else
      if (logEvent.Level == LogLevel.Warn)
      {
        crumb.Type = "Warning";
      }
      else if (logEvent.Level == LogLevel.Error || logEvent.Level == LogLevel.Fatal)
      {
        crumb.Type = "Error";
      }
#endif

      if (userCustomData.Count > 0)
      {
        crumb.CustomData = userCustomData;
      }

      if (!string.IsNullOrEmpty(logEvent.CallerClassName))
      {
        crumb.ClassName = logEvent.CallerClassName;
      }

      if (!string.IsNullOrEmpty(logEvent.CallerMemberName))
      {
        crumb.MethodName = logEvent.CallerMemberName;

        if (logEvent.CallerLineNumber > 0)
        {
          crumb.LineNumber = logEvent.CallerLineNumber;
        }
      }

#if NETFRAMEWORK
      RaygunClient.RecordBreadcrumb(crumb);
#else
      Mindscape.Raygun4Net.Breadcrumbs.RaygunBreadcrumbs.Record(crumb);
#endif
    }

    private List<string> ExtractTags(LogEventInfo logEvent, Exception exception)
    {
      var tags = new List<string>();

      // Try and get tags off the exception data, if they exist
      if (exception?.Data != null)
      {
        if (exception.Data.Contains("Tags"))
        {
          var tagsData = exception.Data["Tags"];
          tags.AddRange(ParseTagsData(tagsData));
        }

        if (exception.Data.Contains("tags"))
        {
          var tagsData = exception.Data["tags"];
          tags.AddRange(ParseTagsData(tagsData));
        }
      }

      // Try and get tags off the properties data, if they exist
      if (logEvent.HasProperties)
      {
        if (logEvent.Properties.TryGetValue("Tags", out var tagsData))
        {
          tags.AddRange(ParseTagsData(tagsData));
        }

        if (logEvent.Properties.TryGetValue("tags", out tagsData))
        {
          tags.AddRange(ParseTagsData(tagsData));
        }
      }

      if (!string.IsNullOrWhiteSpace(Tags))
      {
        var tagsData = SplitValues(Tags);
        tags.AddRange(tagsData);
      }

      return tags;
    }

    private static IEnumerable<string> ParseTagsData(object tagsData)
    {
      switch (tagsData)
      {
        case IEnumerable<string> tagsCollection:
          return tagsCollection;
        case string data:
          return SplitValues(data);
        default:
          return System.Linq.Enumerable.Empty<string>();
      }
    }

    private static string[] SplitValues(string input)
    {
      if (!string.IsNullOrWhiteSpace(input))
      {
        return input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
      }

      return new string[0];
    }

    private static string GetExecutingAssemblyVersion()
    {
      try
      {
        var assembly = System.Reflection.Assembly.GetEntryAssembly();
        return assembly != null ? assembly.GetName().Version.ToString() : null;
      }
      catch (Exception)
      {
        return null;
      }
    }

    private static void SendCompleted(Exception taskException, AsyncContinuation continuation)
    {
      if (taskException != null)
      {
        InternalLogger.Error(taskException, "Raygun: Failed sending logevent.");
      }

      continuation(taskException);
    }
  }
}