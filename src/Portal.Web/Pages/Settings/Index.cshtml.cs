using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Services.Messaging;
using Portal.Web.Services.Tasks;

namespace Portal.Web.Pages.Settings;

/// <summary>
/// Личные настройки — свои у каждого.
///
/// Не путать с «Настройкой портала»: та про весь портал и доступна
/// администратору, эта про себя и доступна всем. Разделение простое:
/// если настройка меняет что-то для коллег — она там, если только
/// для себя — здесь.
/// </summary>
public class IndexModel : PageModel
{
    private readonly UserPreferences _preferences;
    private readonly ConversationService _conversations;

    public IndexModel(UserPreferences preferences, ConversationService conversations)
    {
        _preferences = preferences;
        _conversations = conversations;
    }

    private string UserName => User.Identity?.Name ?? "";

    public IReadOnlyList<(string Id, string Name)> Sounds => UserPreferences.Sounds;

    /// <summary>Звук новых сообщений по умолчанию.</summary>
    public string DefaultSound { get; private set; } = "soft";

    /// <summary>Беседа и выбранный для неё звук. Пусто — как у всех.</summary>
    public sealed record ConversationSound(int Id, string Title, bool IsGroup, string Sound);

    public IReadOnlyList<ConversationSound> Conversations { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var saved = await _preferences.AllAsync(UserName, cancellationToken);

        DefaultSound = saved.TryGetValue(UserPreferences.MessageSound, out var sound)
                       && UserPreferences.IsKnown(sound)
            ? sound
            : "soft";

        var list = await _conversations.ListAsync(UserName, cancellationToken);

        Conversations = list
            .Select(c => new ConversationSound(
                c.Id,
                c.Title,
                c.IsGroup,
                saved.TryGetValue(UserPreferences.SoundFor(c.Id), out var own)
                && UserPreferences.IsKnown(own)
                    ? own
                    : ""))
            .ToList();
    }

    public async Task<IActionResult> OnPostSoundAsync(
        int? conversationId, string? sound, CancellationToken cancellationToken)
    {
        // Чужое значение в запросе ничего не сломает: сохраняем только то,
        // что портал умеет проигрывать.
        var value = UserPreferences.IsKnown(sound) ? sound : null;

        var name = conversationId is { } id
            ? UserPreferences.SoundFor(id)
            : UserPreferences.MessageSound;

        // Для беседы «как у всех» — это отсутствие настройки, а не отдельное
        // значение: иначе, сменив общий звук, человек не понял бы, почему
        // в половине бесед он остался прежним.
        await _preferences.SetAsync(UserName, name, value, cancellationToken);

        StatusMessage = "Звук сохранён.";

        return RedirectToPage();
    }
}
