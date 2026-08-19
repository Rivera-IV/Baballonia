using Baballonia.Contracts;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Baballonia.Services;

public class InferenceFactory
{
    private readonly ILogger<InferenceFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILocalSettingsService _localSettings;

    public InferenceFactory(ILogger<InferenceFactory> logger, ILocalSettingsService localSettings,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _localSettings = localSettings;
        _loggerFactory = loggerFactory;
    }

    public DefaultInferenceRunner Create(string modelPath)
    {
        // Match the UI setting's declared default. On a fresh profile the former false fallback
        // silently loaded the first models on CPU before the settings view had ever been opened.
        var useGpu = _localSettings.ReadSetting<bool>("AppSettings_UseGPU", true);
        _loggerFactory.CreateLogger<DefaultInferenceRunner>();

        var inference = new DefaultInferenceRunner(_loggerFactory);

        inference.Setup(modelPath, useGpu);
        _logger.LogDebug("Loaded model {filename} with hash {ModelHash}", Path.GetFileName(modelPath), Utils.GenerateMD5(modelPath));

        return inference;
    }
}
