namespace iPhoneMiror.Input.Mapping;

/// <summary>Windows virtual-key codes used for special keys (subset of winuser.h VK_*).</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int H = 0x48;
}

public enum KeyAction { None, TypeText, Home }

public readonly record struct KeyTranslation(KeyAction Action, string? Text = null);

/// <summary>
/// Maps special keys to text sent through WebDriverAgent "keys" (which calls XCUIElement typeText).
/// Printable characters are taken from the Windows CharacterReceived event so keyboard layouts and Shift work.
/// Backspace is "\b" (U+0008), the same character WebDriverAgent itself uses to clear text fields.
/// </summary>
public static class KeyboardTranslator
{
    public static KeyTranslation TranslateKey(int virtualKey, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && shift && virtualKey == VirtualKeys.H) return new KeyTranslation(KeyAction.Home);
        if (ctrl || alt) return default;
        return virtualKey switch
        {
            VirtualKeys.Back => new KeyTranslation(KeyAction.TypeText, "\b"),
            VirtualKeys.Enter => new KeyTranslation(KeyAction.TypeText, "\n"),
            VirtualKeys.Tab => new KeyTranslation(KeyAction.TypeText, "\t"),
            _ => default,
        };
    }

    /// <summary>Filters a character from CharacterReceived; control characters are handled by <see cref="TranslateKey"/>.</summary>
    public static string? TranslateCharacter(char c) => char.IsControl(c) ? null : c.ToString();
}
