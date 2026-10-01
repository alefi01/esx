using System.ComponentModel.DataAnnotations;

namespace Portal.Web.Data;

/// <summary>
/// Личная задача (она же заметка со сроком).
///
/// ЗАЧЕМ ЭТО В ПОРТАЛЕ
///
/// Мелкие поручения живут в тетрадях, стикерах на мониторе и переписке —
/// и теряются ровно там же. Отдельную систему задач ради десятка дел
/// в день не ставят, а здесь она рядом с файлами и перепиской, в которых
/// эти дела и рождаются.
///
/// ТРИ СОСТОЯНИЯ, А НЕ ДВА
///
/// Задача живёт в одном из трёх: в работе, выполнена, в архиве.
/// Выполненная не исчезает — она нужна, чтобы помнить, что сделано,
/// и чтобы можно было снять отметку, поставленную по ошибке. В архив
/// её убирают, когда она перестала быть нужной и в списке мешает;
/// удаление — отдельное действие и насовсем.
/// </summary>
public class UserTask
{
    public int Id { get; set; }

    /// <summary>Чья задача. Удалять и править может только он.</summary>
    [MaxLength(256)]
    public string OwnerUserName { get; set; } = "";

    [MaxLength(256)]
    public string OwnerDisplayName { get; set; } = "";

    [Required(ErrorMessage = "Напишите, что нужно сделать")]
    [MaxLength(300, ErrorMessage = "Название не длиннее 300 символов")]
    public string Title { get; set; } = "";

    /// <summary>Подробности. Необязательны: половина задач — это одна строка.</summary>
    [MaxLength(4000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Срок. Необязателен намеренно: задача без срока — это просто
    /// «не забыть», и заставлять выдумывать для неё дату значит
    /// получить список выдуманных дат.
    ///
    /// Хранится как дата без времени (полночь по местному времени
    /// того, кто её поставил): «к пятнице» — обычный срок в работе,
    /// «к пятнице 14:35» — нет.
    /// </summary>
    public DateTime? DueOn { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Когда отметили выполненной. null — ещё в работе.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Когда убрали в архив. null — задача на виду.</summary>
    public DateTime? ArchivedAt { get; set; }

    /// <summary>Кому эта задача ещё видна. Пусто — только автору.</summary>
    public List<UserTaskShare> Shares { get; set; } = [];

    public bool IsDone => CompletedAt is not null;
    public bool IsArchived => ArchivedAt is not null;
}

/// <summary>
/// Доступ к чужой задаче.
///
/// Доступ ТОЛЬКО НА ПРОСМОТР, и это решение, а не упрощение. Общая задача,
/// которую может закрыть любой, — источник вопроса «кто это сделал и сделал
/// ли вообще»: отметку снимают, ставят заново, и к концу недели никто
/// не знает, в каком она состоянии. Здесь у задачи один хозяин, он её
/// и закрывает; остальные видят, что происходит.
/// </summary>
public class UserTaskShare
{
    public int Id { get; set; }

    public int TaskId { get; set; }
    public UserTask? Task { get; set; }

    /// <summary>Логин того, кому показана задача.</summary>
    [MaxLength(256)]
    public string UserName { get; set; } = "";

    [MaxLength(256)]
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// Личная настройка одного человека — звук уведомления и подобное.
///
/// Одна таблица «имя — значение» вместо колонки на каждую настройку:
/// настройки такого рода заводятся и отменяются чаще, чем выходят
/// обновления, и каждая новая колонка означала бы миграцию базы ради
/// одной строки. Значений немного, читаются они разом на человека.
///
/// Что сюда НЕ попадает: облегчённый режим. Он про конкретный компьютер,
/// а не про человека — у одного и того же сотрудника старая машина
/// в кабинете и новая в переговорной. Такое хранится в браузере.
/// </summary>
public class UserPreference
{
    public int Id { get; set; }

    [MaxLength(256)]
    public string UserName { get; set; } = "";

    /// <summary>
    /// Что настраиваем. «sound» — звук новых сообщений, «sound:12» —
    /// звук для беседы №12.
    /// </summary>
    [MaxLength(64)]
    public string Name { get; set; } = "";

    [MaxLength(256)]
    public string Value { get; set; } = "";
}
