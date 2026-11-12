/*
    Страница настольного виджета.

    ЧТО ДЕЛАЕТ

    Показывает окно мессенджера: вкладки, список строк с кружком,
    первой строкой текста, временем и счётчиком. И сообщает
    программе-виджету число для значка у часов и всплывающие окошки
    Windows о новом.

    ОТКУДА ДАННЫЕ

    Чат и объявления — с того же адреса /api/notifications, которым
    живёт колокольчик в портале: своей выборки у виджета нет сознательно,
    иначе на вопрос «что нового» было бы два разных ответа.

    Задачи — с /api/widget/tasks: там не «что нового», а что делать,
    и колокольчик об этом молчит.

    КАК УЗНАЁТ О НОВОМ

    Основной путь — поток событий от сервера (EventSource): о сообщении
    видно сразу, не дожидаясь опроса. Опрос по таймеру остаётся запасным
    на случай оборванного потока, и он редкий — раз в минуту.

    РАБОТАЕТ И В БРАУЗЕРЕ

    Если открыть эту страницу браузером, window.portalWidget просто нет:
    тогда она остаётся обычной страницей со списком.
*/
(function () {
    'use strict';

    var bridge = window.portalWidget || null;

    var list = document.getElementById('wgList');
    var tabsBar = document.getElementById('wgTabs');
    var seen = document.getElementById('wgSeen');
    var hide = document.getElementById('wgHide');
    var name = document.getElementById('wgName');
    var empty = document.getElementById('wgEmpty');
    var emptyText = document.getElementById('wgEmptyText');

    // Какая вкладка открыта. Запоминается на этом компьютере: человек
    // держит виджет открытым днями, и возвращать его к «Всё» при каждом
    // запуске значит каждый раз переключать заново.
    var tab = 'all';

    try {
        tab = localStorage.getItem('portalWidgetTab') || 'all';
    } catch (e) {
        // Хранилище закрыто настройками — не беда, останется «Всё».
    }

    // Последний ответ сервера: по нему перерисовываем список при
    // переключении вкладки, не дёргая сервер заново.
    var last = { items: [], tasks: [], unread: 0, messages: 0, announcements: 0, attention: 0 };

    // Что человек уже видел в этом окне: по этому списку решаем,
    // о чём сообщать окошком Windows.
    var known = null;

    var failures = 0;

    // ------------------------------------------------------------------
    // Мелочи
    // ------------------------------------------------------------------

    function text(value) {
        return value === null || value === undefined ? '' : String(value);
    }

    /** Время события: сегодняшнее — часами, вчерашнее — словом, дальше — датой. */
    function when(value) {
        var at = new Date(value);

        if (isNaN(at.getTime())) {
            return '';
        }

        var now = new Date();
        var days = Math.round(
            (new Date(now.getFullYear(), now.getMonth(), now.getDate()) -
             new Date(at.getFullYear(), at.getMonth(), at.getDate())) / 86400000);

        if (days === 0) {
            return at.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
        }

        if (days === 1) {
            return 'вчера';
        }

        return at.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
    }

    function word(n, one, few, many) {
        var last2 = n % 10;
        var two = n % 100;

        if (two > 10 && two < 20) { return many; }
        if (last2 === 1) { return one; }
        if (last2 > 1 && last2 < 5) { return few; }

        return many;
    }

    /** Одна-две первые буквы имени — как в портале. */
    function initials(name) {
        var parts = text(name).split(/[\s._-]+/).filter(function (p) { return p.length > 0; });

        if (parts.length === 0) { return '?'; }

        return (parts.length === 1
            ? parts[0].slice(0, 1)
            : parts[0].slice(0, 1) + parts[1].slice(0, 1)).toUpperCase();
    }

    /**
     * Цвет кружка по имени.
     *
     * Восемь оттенков по остатку от суммы букв: один и тот же человек
     * всегда одного цвета, и в списке их видно по цвету раньше, чем
     * прочитано имя. Так же сделано в портале и в мессенджерах.
     */
    function tint(name) {
        var sum = 0;

        for (var i = 0; i < name.length; i++) {
            sum += name.charCodeAt(i);
        }

        return 'c' + (sum % 8);
    }

    // ------------------------------------------------------------------
    // Отрисовка
    // ------------------------------------------------------------------

    function rows() {
        if (tab === 'task') {
            return last.tasks.map(taskRow);
        }

        return last.items
            .filter(function (item) { return tab === 'all' || item.kind === tab; })
            .map(eventRow);
    }

    /** Строка чата или объявления — как в мессенджере. */
    function eventRow(item) {
        var row = document.createElement('a');

        row.className = 'wg-row';
        row.href = text(item.url) || '/';
        row.target = '_blank';

        var who = text(item.author) || text(item.title);

        row.appendChild(avatar(who, item.kind));

        var body = document.createElement('div');

        body.className = 'wg-body';

        var top = document.createElement('div');

        top.className = 'wg-top';

        var name = document.createElement('span');

        name.className = 'wg-who';
        name.textContent = item.kind === 'announcement' ? text(item.title) : who;

        var at = document.createElement('time');

        at.className = 'wg-at';
        at.textContent = when(item.at);

        top.appendChild(name);
        top.appendChild(at);

        var line = document.createElement('div');

        line.className = 'wg-line';

        var preview = document.createElement('span');

        preview.className = 'wg-prev';

        // У объявления в первой строке — кто его повесил: в отличие
        // от сообщения, имя в заголовке строки уже занято названием.
        preview.textContent = item.kind === 'announcement'
            ? (text(item.author) + (text(item.preview) ? ': ' + item.preview : ''))
            : text(item.preview);

        line.appendChild(preview);

        // Точки «новое» у строки нет намеренно: в этом списке лежит
        // только непрочитанное, и отметка на каждой строке означала бы
        // ровно ничего.

        body.appendChild(top);
        body.appendChild(line);
        row.appendChild(body);

        return row;
    }

    /** Строка задачи — наше сверх мессенджера. */
    function taskRow(task) {
        var row = document.createElement('a');

        row.className = 'wg-row wg-row--task' + (task.late ? ' late' : '');
        row.href = text(task.url) || '/Tasks';
        row.target = '_blank';

        var sign = document.createElement('span');

        sign.className = 'wg-tick' + (task.kind === 'head' ? ' head' : '');
        sign.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" '
            + 'stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round">'
            + '<path d="M20 6L9 17l-5-5" /></svg>';

        row.appendChild(sign);

        var body = document.createElement('div');

        body.className = 'wg-body';

        var top = document.createElement('div');

        top.className = 'wg-top';

        var name = document.createElement('span');

        name.className = 'wg-who';
        name.textContent = text(task.title);

        top.appendChild(name);

        if (text(task.due)) {
            var due = document.createElement('time');

            due.className = 'wg-at' + (task.late ? ' late' : '');
            due.textContent = task.due;

            top.appendChild(due);
        }

        var line = document.createElement('div');

        line.className = 'wg-line';

        var meta = document.createElement('span');

        meta.className = 'wg-prev';
        meta.textContent = [text(task.who), text(task.state)]
            .filter(function (part) { return part.length > 0; })
            .join(' · ');

        line.appendChild(meta);

        if (task.important) {
            var imp = document.createElement('span');

            imp.className = 'wg-imp';
            imp.textContent = 'важно';

            line.appendChild(imp);
        }

        body.appendChild(top);
        body.appendChild(line);
        row.appendChild(body);

        return row;
    }

    /** Кружок с буквами — цвет по имени, значок для объявления. */
    function avatar(name, kind) {
        var ava = document.createElement('span');

        ava.className = 'wg-ava ' + tint(name);

        if (kind === 'announcement') {
            ava.classList.add('ann');
            ava.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" '
                + 'stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round">'
                + '<path d="M3 11v2a1 1 0 0 0 1 1h3l5 4V6L7 10H4a1 1 0 0 0-1 1z" />'
                + '<path d="M16 9a4 4 0 0 1 0 6" /></svg>';
        } else {
            ava.textContent = initials(name);
        }

        return ava;
    }

    function draw() {
        var built = rows();

        list.textContent = '';

        empty.hidden = built.length > 0;

        if (built.length === 0) {
            emptyText.textContent = tab === 'task' ? 'Задач нет' : 'Нового нет';
        }

        built.forEach(function (row) { list.appendChild(row); });

        paintTabs();

        seen.hidden = (last.unread || 0) === 0;
    }

    /** Заголовок окошка Windows для задачи — по тому, что с ней случилось. */
    function taskWord(task) {
        if (task.kind !== 'head') {
            return task.late ? 'Своя задача просрочена' : 'Своя задача';
        }

        if (task.state === 'Возвращено') {
            return 'Задача возвращена на доработку';
        }

        if (task.late) {
            return 'Задача просрочена';
        }

        return 'Новая задача от ' + (text(task.who) || 'начальника');
    }

    /** Названия вкладок — они же заголовок окна в программе. */
    var TAB_NAMES = {
        all: 'Всё новое',
        message: 'Чат',
        announcement: 'Объявления',
        task: 'Задачи'
    };

    /** Счётчики на вкладках. */
    function paintTabs() {
        // В окне программы в шапке название вкладки, в браузере —
        // название портала: там страницу могли открыть где угодно.
        if (name) {
            name.textContent = bridge
                ? (TAB_NAMES[tab] || '')
                : (name.dataset.portal || 'Портал');
        }

        count('all', last.unread);
        count('message', last.messages);
        count('announcement', last.announcements);
        count('task', last.attention);

        [].forEach.call(tabsBar.querySelectorAll('.wg-tab'), function (button) {
            button.classList.toggle('on', button.dataset.tab === tab);
        });
    }

    function count(name, value) {
        var mark = tabsBar.querySelector('[data-tab="' + name + '"] b');
        var n = value || 0;

        mark.hidden = n === 0;

        if (n > 0) {
            mark.textContent = n > 99 ? '99+' : String(n);
        }
    }

    // ------------------------------------------------------------------
    // Разговор с программой
    // ------------------------------------------------------------------

    function tell() {
        if (!bridge) {
            return;
        }

        var unread = last.unread || 0;
        var attention = last.attention || 0;

        // На значке у часов — непрочитанное. Задачи туда не идут:
        // число, которое висит целыми днями, перестают замечать.
        bridge.status(
            unread,
            unread > 0 ? 'alert' : 'calm',
            unread > 0
                ? unread + ' ' + word(unread, 'новое событие', 'новых события', 'новых событий')
                : (attention > 0
                    ? attention + ' ' + word(attention, 'задача', 'задачи', 'задач') + ' требует внимания'
                    : 'Портал'));

        bridge.alert(unread > 0);

        // Окошком Windows сообщаем обо ВСЁМ, что появилось: о сообщении,
        // об объявлении и о задаче. Задача в этом смысле ничем не хуже
        // сообщения — её точно так же ждут и точно так же пропускают,
        // если о ней не сказать.
        //
        // У задачи в ключ входит ещё и состояние: возвращённая
        // на доработку — это новое событие, хотя задача та же.
        var fresh = (last.items || []).map(function (item) {
            return {
                key: item.kind + item.id,
                title: text(item.title) || 'Портал',
                body: text(item.preview) || text(item.author)
            };
        }).concat((last.tasks || []).map(function (task) {
            return {
                key: 'task' + task.kind + task.id + task.state,
                title: taskWord(task),
                body: text(task.title) + (text(task.due) ? ' — ' + task.due : '')
            };
        }));

        // Первый ответ после открытия окна только запоминаем: сообщать
        // окошками о том, что лежало и до запуска программы, — значит
        // засыпать человека уведомлениями при каждом входе в Windows.
        if (known === null) {
            known = {};

            fresh.forEach(function (item) { known[item.key] = true; });

            return;
        }

        fresh.slice().reverse().forEach(function (item) {
            if (known[item.key]) {
                return;
            }

            known[item.key] = true;

            bridge.notify(item.title, item.body);
        });
    }

    // ------------------------------------------------------------------
    // Опрос
    // ------------------------------------------------------------------

    function ask(url) {
        return fetch(url, {
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        }).then(function (response) {
            if (response.status === 401 || response.status === 403) {
                // Вход истёк. Белый список без объяснений выглядит как
                // поломка, поэтому отправляем человека входить —
                // в полном окне, где есть форма.
                window.open('/Account/Login', '_blank');

                throw 0;
            }

            if (!response.ok) {
                throw response.status;
            }

            return response.json();
        });
    }

    function load() {
        return Promise.all([ask('/api/notifications'), ask('/api/widget/tasks')])
            .then(function (answers) {
                var data = answers[0];

                failures = 0;

                last = {
                    items: data.items || [],
                    tasks: answers[1] || [],
                    unread: data.unread || 0,
                    messages: data.messages || 0,
                    announcements: data.announcements || 0,
                    attention: data.tasks || 0
                };

                draw();
                tell();
            })
            .catch(function () {
                failures += 1;

                // Молчим о единичной осечке: сеть в офисе мигает, и менять
                // из-за этого список на сообщение об ошибке не стоит.
                if (failures >= 3 && list.childElementCount === 0) {
                    empty.hidden = false;
                    emptyText.textContent = 'Портал не отвечает. Пробуем снова…';
                }
            });
    }

    // ------------------------------------------------------------------
    // Кнопки
    // ------------------------------------------------------------------

    tabsBar.addEventListener('click', function (e) {
        var button = e.target.closest('.wg-tab');

        if (!button) {
            return;
        }

        tab = button.dataset.tab;

        try {
            localStorage.setItem('portalWidgetTab', tab);
        } catch (err) {
            // Не запомнилось — переживём.
        }

        draw();

        list.scrollTop = 0;
    });

    seen.addEventListener('click', function () {
        var field = document.querySelector('#wgForm input[name="__RequestVerificationToken"]');
        var body = new FormData();

        body.append('__RequestVerificationToken', field ? field.value : '');

        fetch('/api/notifications/seen', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            body: body
        }).then(load, load);
    });

    hide.addEventListener('click', function () {
        if (bridge) {
            bridge.hide();
        }
    });

    // Перетаскивание за шапку: своей рамки у окна виджета нет.
    // Двигает окно сама программа — страница только сообщает о нажатии.
    document.querySelector('[data-drag]').addEventListener('mousedown', function (e) {
        if (e.button !== 0 || e.target.closest('.wg-btn')) {
            return;
        }

        if (bridge) {
            bridge.drag();
        }
    });

    // ------------------------------------------------------------------
    // Поток событий
    // ------------------------------------------------------------------

    function listen() {
        if (typeof EventSource === 'undefined') {
            return;
        }

        var stream = new EventSource('/api/notifications/stream');

        stream.onmessage = function () { load(); };

        stream.onerror = function () {
            // Браузер переподключается сам; если соединение закрылось
            // окончательно — поднимем заново через минуту, а до тех пор
            // работает опрос.
            if (stream.readyState === 2) {
                setTimeout(listen, 60000);
            }
        };
    }

    paintTabs();
    load();
    listen();

    // Запасной опрос. Редкий: основное приходит потоком.
    setInterval(load, 60000);
}());
