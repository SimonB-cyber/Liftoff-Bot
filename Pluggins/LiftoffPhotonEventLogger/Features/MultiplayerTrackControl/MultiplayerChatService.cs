using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;

internal sealed class MultiplayerChatService
{
    private static readonly string[] ChatTypeNames =
    {
        "Liftoff.Multiplayer.Chat.ChatHistory",
        "Liftoff.Multiplayer.Chat.ChatWindowPanel",
        "Liftoff.Multiplayer.Chat.MessageEntryPanel",
        "Liftoff.Multiplayer.Chat.ChatToggle"
    };

    private static readonly string[] SendMethodNames =
    {
        "SendUserMessage",
        "OnSubmit",
        "OnInputFieldEditEnded",
        "OnChatValueChange",
        "SendMessage",
        "SubmitMessage",
        "OnSend",
        "PostMessage",
        "Send",
        "Submit"
    };

    private static readonly string[] ChatToggleTypeNames =
    {
        "Liftoff.Multiplayer.Chat.ChatToggle"
    };

    private static readonly string[] ShowChatMethodNames =
    {
        "ShowChat",
        "OnShowChat",
        "Show"
    };

    private static readonly string[] CandidateSubmitButtonNames =
    {
        "send",
        "submit",
        "chat",
        "post"
    };

    private static readonly string[] PreferredInputFieldNames =
    {
        "input",
        "message",
        "chat"
    };

    private readonly MultiplayerTrackControlConfig _config;
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;

    public MultiplayerChatService(
        MultiplayerTrackControlConfig config,
        MultiplayerDiscoveryService discovery,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _config = config;
        _discovery = discovery;
        _log = log;
        _describe = describe;
    }

    public void DumpChatState()
    {
        foreach (var typeName in ChatTypeNames)
        {
            var type = ResolveType(typeName);
            if (type == null)
            {
                _log.Warn("CHAT", $"Known chat type missing: {typeName}");
                continue;
            }

            _log.Info("CHAT", $"Known chat type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            foreach (var liveObject in ReflectionHelper.GetLiveObjects(type).Take(3))
            {
                _log.Info("CHAT", $"  live {ReflectionHelper.DescribeObjectIdentity(liveObject)}");
                _log.Info("CHAT", $"  snapshot {ReflectionHelper.SafeDescribe(_describe, liveObject)}");
                DumpInterestingMembers(liveObject);
                DumpMessageCollection(liveObject);
            }
        }
    }

    public bool SendConfiguredMessage()
    {
        var text = _config.PendingChatMessage.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Warn("CHAT", "PendingChatMessage is empty. Nothing to send.");
            return false;
        }

        return TrySendMessage(text, source: "config");
    }

    public bool SendRaw(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return TrySendMessage(message.Trim(), source: "server");
    }

    public bool SendAnnouncement(string environmentName, string trackName, string raceName, string workshopId)
    {
        var template = _config.ChatMessageTemplate.Value ?? "Switching to {environment} / {race}";
        var message = template
            .Replace("{environment}", environmentName ?? string.Empty)
            .Replace("{track}", trackName ?? string.Empty)
            .Replace("{race}", raceName ?? string.Empty)
            .Replace("{workshop}", workshopId ?? string.Empty);

        if (string.IsNullOrWhiteSpace(message))
            return false;

        return TrySendMessage(message.Trim(), source: "announcement");
    }

    private bool TrySendMessage(string message, string source)
    {
        _discovery.Refresh();
        _log.Info("CHAT", $"Attempting to send chat message via {source}: \"{message}\"");
        EnsureChatWindowVisible();

        foreach (var panel in GetLiveChatObjects("Liftoff.Multiplayer.Chat.ChatWindowPanel"))
        {
            if (TrySendViaPanel(panel, message))
                return true;
        }

        foreach (var panel in GetLiveChatObjects("Liftoff.Multiplayer.Chat.MessageEntryPanel"))
        {
            if (TrySendViaPanel(panel, message))
                return true;
        }

        _log.Warn("CHAT", "No working chat send path was found. TODO: validate chat sender methods at runtime.");
        return false;
    }

    private bool TrySendViaPanel(object panel, string message)
    {
        _log.Info("CHAT", $"Trying chat sender on {ReflectionHelper.DescribeObjectIdentity(panel)}");
        var populatedLiveInput = TryPopulateLiveInputFields(panel, message);
        if (!populatedLiveInput)
            TryPopulateKnownTextFields(panel, message);
        DumpDeclaredSendCandidates(panel);

        if (TryInvokeKnownChatWindowFlow(panel, message))
            return true;

        foreach (var methodName in SendMethodNames)
        {
            if (TryInvokeChatMethod(panel, methodName, message))
                return true;
        }

        var submittedViaInput = TrySubmitInputFieldEvents(panel, message);
        var submittedViaButton = TryClickChildSubmitButton(panel);
        if (submittedViaInput && submittedViaButton)
        {
            _log.Info("CHAT", "Completed chat submit via input end-edit pipeline and send button.");
            return true;
        }

        if (submittedViaButton)
        {
            _log.Info("CHAT", "Completed chat submit via send button after populating live input.");
            return true;
        }

        if (submittedViaInput)
        {
            _log.Warn("CHAT", "Invoked the live chat input end-edit pipeline, but no real send button was triggered afterward.");
            return true;
        }

        return false;
    }

    private bool TryInvokeKnownChatWindowFlow(object panel, string message)
    {
        var panelType = panel.GetType();
        if (!string.Equals(panelType.FullName, "Liftoff.Multiplayer.Chat.ChatWindowPanel", StringComparison.Ordinal))
            return false;

        var invoked = false;

        if (TryInvokeZeroArgMethod(panel, "GiveFocus"))
            invoked = true;

        if (TryInvokeNamedChatMethod(panel, "OnChatValueChange", message))
            invoked = true;

        if (TryInvokeNamedChatMethod(panel, "OnInputFieldEditEnded", message))
            invoked = true;

        if (TryInvokeNamedChatMethod(panel, "SendUserMessage", message))
        {
            _log.Info("CHAT", "Completed chat submit via ChatWindowPanel.SendUserMessage().");
            return true;
        }

        return invoked;
    }

    private bool TryPopulateLiveInputFields(object panel, string message)
    {
        if (panel is not Component component)
            return false;

        var populated = false;
        var inputFields = component.GetComponentsInChildren<InputField>(true)
            .OrderByDescending(input => ScoreInputField(input.name))
            .ToList();

        foreach (var inputField in inputFields)
        {
            inputField.text = message;
            inputField.onValueChanged?.Invoke(message);
            _log.Info("CHAT", $"Populated child InputField on {ReflectionHelper.DescribeObjectIdentity(inputField)} name=\"{inputField.name}\".");
            populated = true;
        }

        TryPopulateTextMeshProChildren(component, message);
        return populated;
    }

    private bool TrySubmitInputFieldEvents(object panel, string message)
    {
        if (panel is not Component component)
            return false;

        var submitted = false;
        foreach (var inputField in component.GetComponentsInChildren<InputField>(true)
                     .OrderByDescending(input => ScoreInputField(input.name)))
        {
            try
            {
                inputField.ActivateInputField();
                inputField.MoveTextEnd(false);
                inputField.onEndEdit?.Invoke(message);
                inputField.DeactivateInputField();
                _log.Info("CHAT", $"Invoked InputField end-edit pipeline on {ReflectionHelper.DescribeObjectIdentity(inputField)} name=\"{inputField.name}\".");
                submitted = true;
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"Failed to submit InputField on {inputField.name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var tmpInputType = ResolveType("TMPro.TMP_InputField");
        if (tmpInputType != null)
        {
            foreach (var tmpInput in component.GetComponentsInChildren(tmpInputType, true))
            {
                try
                {
                    TryInvokeZeroArgMethod(tmpInput, "ActivateInputField");
                    TryInvokeZeroArgMethod(tmpInput, "MoveTextEnd");

                    var onEndEdit = ReflectionHelper.GetMemberValue(tmpInput, "onEndEdit");
                    var invoke = onEndEdit == null ? null : ReflectionHelper.FindMethod(onEndEdit.GetType(), "Invoke", 1);
                    if (invoke != null)
                    {
                        invoke.Invoke(onEndEdit, new object[] { message });
                        _log.Info("CHAT", $"Invoked TMP_InputField end-edit pipeline on {ReflectionHelper.DescribeObjectIdentity(tmpInput)}.");
                        submitted = true;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("CHAT", $"Failed to submit TMP_InputField on {ReflectionHelper.DescribeObjectIdentity(tmpInput)}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return submitted;
    }

    private void TryPopulateKnownTextFields(object panel, string message)
    {
        foreach (var memberName in new[] { "message", "Message", "text", "Text", "currentMessage", "CurrentMessage" })
            TrySetTextMember(panel, memberName, message);

        foreach (var holderName in new[] { "inputField", "messageInput", "input", "chatInput", "txtMessage" })
        {
            var holder = ReflectionHelper.GetMemberValue(panel, holderName);
            if (holder == null)
                continue;

            if (holder is InputField inputField)
            {
                inputField.text = message;
                _log.Info("CHAT", $"Populated UnityEngine.UI.InputField via {holderName}.");
                continue;
            }

            TrySetTextMember(holder, "text", message);
            TrySetTextMember(holder, "Text", message);
        }

        if (panel is Component component)
        {
            foreach (var inputField in component.GetComponentsInChildren<InputField>(true))
            {
                inputField.text = message;
                _log.Info("CHAT", $"Populated child InputField on {ReflectionHelper.DescribeObjectIdentity(inputField)}.");
            }

            foreach (var textComponent in component.GetComponentsInChildren<Text>(true))
            {
                if (!LooksLikeChatTextTarget(textComponent))
                    continue;

                textComponent.text = message;
                _log.Info("CHAT", $"Populated child Text on {ReflectionHelper.DescribeObjectIdentity(textComponent)} name=\"{textComponent.name}\".");
            }

            TryPopulateTextMeshProChildren(component, message);
        }
    }

    private void TrySetTextMember(object instance, string memberName, string text)
    {
        try
        {
            if (ReflectionHelper.SetMemberValue(instance, memberName, text))
                _log.Info("CHAT", $"Set {instance.GetType().FullName}.{memberName} for chat send.");
        }
        catch (Exception ex)
        {
            _log.Warn("CHAT", $"Failed to set {instance.GetType().FullName}.{memberName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryInvokeChatMethod(object panel, string methodName, string message)
    {
        var methods = panel.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName)
            .Where(IsSupportedChatMethod)
            .OrderBy(method => method.GetParameters().Length)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    method.Invoke(panel, Array.Empty<object>());
                    _log.Info("CHAT", $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
                    return true;
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    method.Invoke(panel, new object[] { message });
                    _log.Info("CHAT", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with message text.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"{methodName} invocation failed on {panel.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private bool TryInvokeNamedChatMethod(object panel, string methodName, string message)
    {
        var methods = panel.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName)
            .Where(IsSupportedChatMethod)
            .OrderBy(method => method.GetParameters().Length)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    method.Invoke(panel, Array.Empty<object>());
                    _log.Info("CHAT", $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
                    return true;
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    method.Invoke(panel, new object[] { message });
                    _log.Info("CHAT", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with message text.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"{methodName} invocation failed on {panel.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private void EnsureChatWindowVisible()
    {
        foreach (var typeName in ChatToggleTypeNames)
        {
            foreach (var toggle in GetLiveChatObjects(typeName))
            {
                try
                {
                    var isHidden = ReflectionHelper.GetMemberValue(toggle, "IsChatWindowHidden");
                    if (!ReflectionHelper.IsTruthy(isHidden))
                        return;

                    foreach (var methodName in ShowChatMethodNames)
                    {
                        var method = ReflectionHelper.FindDeclaredMethod(toggle.GetType(), methodName, 0);
                        if (method == null || !IsSupportedChatMethod(method))
                            continue;

                        method.Invoke(toggle, Array.Empty<object>());
                        _log.Info("CHAT", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} to reveal the chat window before sending.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("CHAT", $"Failed to reveal chat window via {ReflectionHelper.DescribeObjectIdentity(toggle)}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private void DumpDeclaredSendCandidates(object panel)
    {
        var methods = panel.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => SendMethodNames.Contains(method.Name, StringComparer.Ordinal))
            .Where(IsSupportedChatMethod)
            .OrderBy(method => method.Name)
            .ThenBy(method => method.GetParameters().Length)
            .Select(ReflectionHelper.FormatMethodSignature)
            .Distinct()
            .ToList();

        if (methods.Count == 0)
        {
            _log.Warn("CHAT", $"No declared chat send candidates were found on {panel.GetType().FullName}.");
            return;
        }

        foreach (var method in methods)
            _log.Info("CHAT", $"Declared send candidate: {method}");
    }

    private static bool IsSupportedChatMethod(MethodInfo method)
    {
        var declaringType = method.DeclaringType;
        if (declaringType == null)
            return false;

        if (declaringType.Namespace != null &&
            declaringType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
            return false;

        return true;
    }

    private void DumpInterestingMembers(object liveObject)
    {
        var members = liveObject.GetType()
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member.Name.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             member.Name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             member.Name.IndexOf("history", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             member.Name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(12)
            .ToList();

        foreach (var member in members)
            _log.Info("CHAT", $"  member {liveObject.GetType().FullName}.{member.Name}");

        if (liveObject is Component component)
            DumpInterestingChildComponents(component);
    }

    private void DumpMessageCollection(object liveObject)
    {
        foreach (var memberName in new[] { "messages", "Messages", "history", "History", "entries", "Entries" })
        {
            var value = ReflectionHelper.GetMemberValue(liveObject, memberName);
            var items = ReflectionHelper.EnumerateAsObjects(value).Take(8).ToList();
            if (items.Count == 0)
                continue;

            _log.Info("CHAT", $"  {memberName} count~{items.Count}");
            for (var index = 0; index < items.Count; index++)
                _log.Info("CHAT", $"    [{index}] {ReflectionHelper.SafeDescribe(_describe, items[index])}");
        }
    }

    private IEnumerable<object> GetLiveChatObjects(string fullTypeName)
    {
        var type = ResolveType(fullTypeName);
        return ReflectionHelper.GetLiveObjects(type);
    }

    private bool TryClickChildSubmitButton(object panel)
    {
        if (panel is not Component component)
            return false;

        var buttons = component.GetComponentsInChildren<Button>(true)
            .Where(button => CandidateSubmitButtonNames.Any(token =>
                button.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                button.gameObject.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        foreach (var button in buttons)
        {
            try
            {
                button.onClick?.Invoke();
                _log.Info("CHAT", $"Invoked Button.onClick on {ReflectionHelper.DescribeObjectIdentity(button)} name=\"{button.name}\".");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("CHAT", $"Failed to invoke Button.onClick on {button.name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private void TryPopulateTextMeshProChildren(Component component, string message)
    {
        var tmpInputType = ResolveType("TMPro.TMP_InputField");
        if (tmpInputType != null)
        {
            foreach (var tmpInput in component.GetComponentsInChildren(tmpInputType, true))
            {
                if (ReflectionHelper.SetMemberValue(tmpInput, "text", message))
                    _log.Info("CHAT", $"Populated TMP_InputField text on {ReflectionHelper.DescribeObjectIdentity(tmpInput)}.");

                TryInvokeZeroArgMethod(tmpInput, "ActivateInputField");
                TryInvokeZeroArgMethod(tmpInput, "MoveTextEnd");
            }
        }

        var tmpTextType = ResolveType("TMPro.TMP_Text");
        if (tmpTextType == null)
            return;

        foreach (var tmpText in component.GetComponentsInChildren(tmpTextType, true))
        {
            var name = (tmpText as Component)?.name ?? tmpText.GetType().Name;
            if (!LooksLikeChatTextTargetName(name))
                continue;

            if (ReflectionHelper.SetMemberValue(tmpText, "text", message))
                _log.Info("CHAT", $"Populated TMP_Text on {ReflectionHelper.DescribeObjectIdentity(tmpText)} name=\"{name}\".");
        }
    }

    private bool TryInvokeZeroArgMethod(object instance, string methodName)
    {
        try
        {
            var method = ReflectionHelper.FindDeclaredMethod(instance.GetType(), methodName, 0);
            if (method == null)
                return false;

            method.Invoke(instance, Array.Empty<object>());
            _log.Info("CHAT", $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("CHAT", $"Failed to invoke {instance.GetType().FullName}.{methodName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void DumpInterestingChildComponents(Component component)
    {
        var children = component.GetComponentsInChildren<Component>(true)
            .Where(child =>
            {
                var typeName = child.GetType().FullName ?? child.GetType().Name;
                return typeName.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       typeName.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       typeName.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child.name.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child.name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child.name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       child is InputField ||
                       child is Button ||
                       child is Text;
            })
            .Take(20)
            .ToList();

        foreach (var child in children)
            _log.Info("CHAT", $"  child {ReflectionHelper.DescribeObjectIdentity(child)} name=\"{child.name}\" type={child.GetType().FullName}");
    }

    private static bool LooksLikeChatTextTarget(Text textComponent)
    {
        return LooksLikeChatTextTargetName(textComponent.name);
    }

    private static bool LooksLikeChatTextTargetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.IndexOf("prefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("missed", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("header", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        return name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("chatinput", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("messageinput", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ScoreInputField(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;

        var score = 0;
        foreach (var token in PreferredInputFieldNames)
        {
            if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 10;
        }

        if (name.IndexOf("send", StringComparison.OrdinalIgnoreCase) >= 0)
            score -= 5;

        return score;
    }

    private Type? ResolveType(string fullTypeName)
    {
        var type = _discovery.TryResolveKnownType(fullTypeName);
        if (type != null)
            return type;

        var simpleName = fullTypeName.Split('.').Last();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] loadableTypes;
            try
            {
                loadableTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                loadableTypes = ex.Types.Where(candidate => candidate != null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            type = loadableTypes.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, fullTypeName, StringComparison.Ordinal) ||
                string.Equals(candidate.Name, simpleName, StringComparison.Ordinal));
            if (type != null)
                return type;
        }

        return null;
    }
}
