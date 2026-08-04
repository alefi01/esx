using System.ComponentModel.DataAnnotations;

namespace Portal.Web.Data;

/// <summary>
/// Объявление в ленте.
///
/// Это «сущность» — класс, одному экземпляру которого соответствует одна строка
/// в таблице базы данных. Entity Framework сам построит по нему таблицу
/// (см. папку Migrations) и сам будет превращать строки в объекты и обратно.
///
/// Атрибуты вида [Required] и [MaxLength] работают сразу в двух местах:
/// по ним EF задаёт тип колонки и ограничения в базе, и по ним же
/// ASP.NET Core проверяет данные из формы, прежде чем что-то сохранять.
/// </summary>
public class Announcement
{
    /// <summary>
    /// Первичный ключ. Значение выдаёт база данных (последовательность),
    /// в коде его заполнять не нужно.
    /// </summary>
    public int Id { get; set; }

    [Required(ErrorMessage = "Введите заголовок")]
    [MaxLength(200, ErrorMessage = "Заголовок не длиннее 200 символов")]
    [Display(Name = "Заголовок")]
    public string Title { get; set; } = "";

    /// <summary>
    /// Текст объявления. Хранится и вводится как обычный текст, без разметки —
    /// см. пояснение в PlainTextFormatter о том, почему не HTML-редактор.
    /// </summary>
    [Required(ErrorMessage = "Введите текст объявления")]
    [MaxLength(10000, ErrorMessage = "Текст не длиннее 10000 символов")]
    [Display(Name = "Текст")]
    public string Body { get; set; } = "";

    /// <summary>
    /// Логин автора (sAMAccountName). Именно по нему определяется,
    /// может ли текущий пользователь править это объявление.
    /// Ссылки на таблицу пользователей нет — своего справочника мы не ведём,
    /// пользователи живут в Active Directory.
    /// </summary>
    [MaxLength(256)]
    public string AuthorUserName { get; set; } = "";

    /// <summary>
    /// ФИО автора на момент публикации. Сохраняется отдельно намеренно:
    /// если человек уволится и его учётку удалят, объявление всё равно
    /// останется подписанным, а не превратится в «неизвестный автор».
    /// </summary>
    [MaxLength(256)]
    public string AuthorDisplayName { get; set; } = "";

    /// <summary>
    /// Когда опубликовано. ВСЕГДА в UTC (Kind = Utc).
    ///
    /// Почему UTC, а не местное время: местное время меняется при переходе
    /// на летнее и обратно, и один и тот же момент можно записать двумя
    /// способами. В базе PostgreSQL это колонка timestamp with time zone.
    /// В местное время значение переводится только при показе,
    /// вызовом ToLocalTime() в разметке страницы.
    ///
    /// Тип DateTime, а не DateTimeOffset, выбран сознательно: SQLite,
    /// на котором работают автотесты, не умеет сортировать по DateTimeOffset,
    /// и лента в тестах проверялась бы не тем кодом, который работает на сервере.
    /// Смещение нам всё равно не нужно — время всегда UTC.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Когда последний раз правили, тоже в UTC. null — не правили ни разу.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Приложенные файлы и картинки.</summary>
    public List<AnnouncementFile> Files { get; set; } = [];
}

/// <summary>
/// Файл, приложенный к объявлению.
///
/// Лежит отдельной веткой на диске, а не в файловом хранилище: доступ там
/// решается группами Active Directory, а объявление по смыслу видно всем,
/// у кого есть доступ к порталу. Смешивать две разные модели доступа
/// в одном месте — верный способ однажды показать не то и не тому.
/// </summary>
public class AnnouncementFile
{
    public int Id { get; set; }

    public int AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }

    /// <summary>Имя, которое видит человек.</summary>
    [MaxLength(260)]
    public string OriginalName { get; set; } = "";

    /// <summary>
    /// Имя на диске — случайное. Имя, данное человеком, в пути к файлу
    /// не участвует никогда.
    /// </summary>
    [MaxLength(64)]
    public string StorageName { get; set; } = "";

    public long SizeBytes { get; set; }

    [MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Картинка ли это. Считается один раз при загрузке, по белому списку
    /// расширений: от этого зависит, показать ли файл прямо в ленте
    /// или дать ссылку на скачивание.
    /// </summary>
    public bool IsImage { get; set; }
}
