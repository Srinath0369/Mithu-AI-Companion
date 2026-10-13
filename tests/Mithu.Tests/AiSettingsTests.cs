using Microsoft.Extensions.Logging.Abstractions;
using Mithu.App.Services;
using Mithu.App.ViewModels;
using Mithu.Core.Ai;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;

namespace Mithu.Tests;

internal sealed class MemorySettings : ISettingsStore
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        Changed?.Invoke(this, Current);
    }
}

internal sealed class MemorySecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Values.Remove(name);
        }
        else
        {
            Values[name] = value;
        }
    }
}

/// <summary>Records connections and answers the model list (or throws).</summary>
internal sealed class FakeAiFactory : IAiClientFactory
{
    public List<AiConnection> Connections { get; } = [];

    public Func<IReadOnlyList<string>> Models { get; set; } = () => ["claude-haiku-5-5", "claude-opus-5-5"];

    /// <summary>What a chat request returns (or throws); records each request.</summary>
    public Func<AiRequest, CancellationToken, Task<AiResponse>> Reply { get; set; } =
        (_, _) => Task.FromResult(new AiResponse(AiMessage.Assistant("ok"), AiStopReason.Done));

    public List<AiRequest> Requests { get; } = [];

    /// <summary>A client to hand out instead of the scripted one (e.g. a streaming fake).</summary>
    public IAiClient? Custom { get; set; }

    public IAiClient Create(AiConnection connection)
    {
        Connections.Add(connection);
        return Custom ?? new Client(this);
    }

    private sealed class Client(FakeAiFactory owner) : IAiClient
    {
        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            owner.Requests.Add(request);
            return owner.Reply(request, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.Models());
    }
}

public class AiSettingsTests
{
    private readonly MemorySettings _settings = new();
    private readonly MemorySecrets _secrets = new();
    private readonly FakeAiFactory _factory = new();

    private AiService Service() => new(_settings, _secrets, _factory, NullLogger<AiService>.Instance);

    [Fact]
    public void Ai_is_off_by_default()
    {
        Assert.False(Service().IsReady);
        Assert.False(new AppSettings().Ai.Enabled);
        Assert.False(new AppSettings().Ai.AllowNotes);
    }

    [Fact]
    public async Task Test_needs_a_key_for_cloud_providers()
    {
        var result = await Service().TestAsync(new AiSettings(), key: null);
        Assert.False(result.Ok);
        Assert.Empty(_factory.Connections); // nothing contacted
    }

    [Fact]
    public async Task Test_lists_models_with_the_typed_key()
    {
        var result = await Service().TestAsync(new AiSettings(), "sk-ant-typed");
        Assert.True(result.Ok);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("sk-ant-typed", _factory.Connections.Single().ApiKey);
    }

    [Fact]
    public async Task Test_uses_the_saved_key_when_none_is_typed()
    {
        _secrets.Set(AiSettings.KeyName("claude"), "sk-ant-saved");
        await Service().TestAsync(new AiSettings(), key: null);
        Assert.Equal("sk-ant-saved", _factory.Connections.Single().ApiKey);
    }

    [Fact]
    public async Task A_server_without_a_model_list_still_counts_as_reachable()
    {
        _factory.Models = () => throw new AiException(AiErrorKind.NotFound, "nope");
        var local = new AiSettings { PresetId = "ollama", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:11434/v1" };
        var result = await Service().TestAsync(local, key: null);
        Assert.True(result.Ok);
        Assert.Contains("type the model name", result.Message);
    }

    [Fact]
    public async Task A_bad_key_is_reported()
    {
        _factory.Models = () => throw new AiException(AiErrorKind.InvalidKey, AiException.Friendly(AiErrorKind.InvalidKey));
        var result = await Service().TestAsync(new AiSettings(), "sk-wrong");
        Assert.False(result.Ok);
        Assert.Contains("API key didn't work", result.Message);
    }

    [Fact]
    public void Key_hint_shows_only_the_last_four()
    {
        var ai = Service();
        ai.SetKey("claude", "  sk-ant-abcdef1234  ");
        Assert.Equal("••••1234", ai.KeyHint("claude"));
        Assert.Null(ai.KeyHint("openai"));
    }

    // ===== Settings page ======================================================

    private AiSettingsViewModel Page() => new(Service());

    [Fact]
    public void Turning_on_the_first_time_asks_for_consent_then_saves_key_and_choices()
    {
        var page = Page();
        Assert.False(page.CanTurnOn); // Claude needs a key
        page.KeyDraft = "sk-ant-new";
        page.Model = "claude-haiku-5-5";

        page.TurnOnCommand.Execute(null);
        Assert.True(page.ShowConsent);
        Assert.False(_settings.Current.Ai.Enabled);
        Assert.Empty(_secrets.Values); // nothing saved before consent

        page.AcceptConsentCommand.Execute(null);
        Assert.False(page.ShowConsent);
        Assert.True(page.IsOn);
        Assert.True(_settings.Current.Ai.ConsentGiven);
        Assert.Equal("claude-haiku-5-5", _settings.Current.Ai.Model);
        Assert.Equal("sk-ant-new", _secrets.Get(AiSettings.KeyName("claude")));
        Assert.Equal(string.Empty, page.KeyDraft); // the box is emptied once saved
        Assert.Contains("Haiku", page.StateText.Replace("claude-haiku-5-5", "Haiku"));
    }

    [Fact]
    public void Local_servers_need_no_key()
    {
        var page = Page();
        page.Providers.Single(p => p.Key == "ollama").SelectCommand.Execute(null);
        Assert.Equal("http://localhost:11434/v1", page.BaseUrl);
        Assert.False(page.NeedsKey);
        Assert.False(page.CanTurnOn); // no model yet
        page.Model = "llama3";
        Assert.True(page.CanTurnOn);
    }

    [Fact]
    public void Other_servers_show_the_key_field_but_do_not_require_it()
    {
        var page = Page();
        page.Providers.Single(p => p.Key == "custom").SelectCommand.Execute(null);
        Assert.True(page.AcceptsKey);
        Assert.False(page.NeedsKey);
        page.BaseUrl = "http://localhost:20128/v1";
        page.Model = "some-model";
        Assert.True(page.CanTurnOn);
    }

    [Fact]
    public void Turn_off_and_remove_key()
    {
        _secrets.Set(AiSettings.KeyName("claude"), "sk-ant-saved");
        _settings.Update(s => s with { Ai = s.Ai with { Enabled = true, ConsentGiven = true } });
        var page = Page();
        Assert.True(page.IsOn);
        Assert.True(page.HasSavedKey);

        page.TurnOffCommand.Execute(null);
        Assert.False(page.IsOn);

        page.TurnOnCommand.Execute(null); // consent already given: straight on
        Assert.True(page.IsOn);

        page.RemoveKeyCommand.Execute(null);
        Assert.False(page.HasSavedKey);
        Assert.False(page.IsOn); // no key, no AI
    }

    [Fact]
    public void Switching_provider_remembers_each_saved_choice()
    {
        _settings.Update(s => s with { Ai = s.Ai with { Model = "claude-sonnet-5-5" } });
        var page = Page();
        page.Providers.Single(p => p.Key == "openai").SelectCommand.Execute(null);
        Assert.Equal("https://api.openai.com/v1", page.BaseUrl);
        Assert.Equal(string.Empty, page.Model);

        page.Providers.Single(p => p.Key == "claude").SelectCommand.Execute(null);
        Assert.Equal("claude-sonnet-5-5", page.Model);
    }

    [Fact]
    public void Notes_access_applies_at_once()
    {
        var page = Page();
        page.AllowNotes = true;
        Assert.True(_settings.Current.Ai.AllowNotes);
    }

    [Fact]
    public async Task Test_fills_model_suggestions_that_filter_as_you_type()
    {
        _factory.Models = () => ["gpt-x-mini", "gpt-x", "other-model"];
        var page = Page();
        page.Providers.Single(p => p.Key == "openai").SelectCommand.Execute(null);
        page.KeyDraft = "sk-test";

        await page.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(page.TestOk);
        Assert.Equal("gpt-x-mini", page.Model); // nothing chosen yet → first listed
        Assert.Equal(3, page.ModelSuggestions.Count); // an exact pick shows everything

        page.Model = "gpt";
        Assert.Equal(["gpt-x-mini", "gpt-x"], page.ModelSuggestions);
    }

    [Fact]
    public async Task Model_dropdown_opens_after_test_and_while_typing_and_closes_on_pick()
    {
        _factory.Models = () => Enumerable.Range(1, 60).Select(i => $"model-{i:00}").ToList();
        var page = Page();
        page.Providers.Single(p => p.Key == "openai").SelectCommand.Execute(null);
        page.KeyDraft = "sk-test";

        await page.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(page.IsModelListOpen);
        Assert.Equal("Showing 40 of 60 — type to narrow", page.ModelListNote);
        Assert.Single(page.ModelOptions, o => o.IsSelected); // the auto-picked first model is ticked

        page.ChooseModelCommand.Execute("model-07");
        Assert.False(page.IsModelListOpen);
        Assert.Equal("model-07", page.Model);

        page.Model = "model-5";
        Assert.True(page.IsModelListOpen);
        Assert.Equal(10, page.ModelOptions.Count);
        Assert.False(page.HasModelListNote);

        page.Model = "nothing-like-it";
        Assert.Contains("No listed model matches", page.ModelListNote);

        page.ToggleModelListCommand.Execute(null);
        Assert.False(page.IsModelListOpen);
    }
}
