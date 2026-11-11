/*
    Страница настольного виджета.

    ЧТО ДЕЛАЕТ

    Спрашивает у портала «что нового» и показывает это списком, а заодно
    сообщает программе-виджету число для значка у часов и всплывающие
    окошки Windows о новом.

    ОТКУДА ДАННЫЕ

    С того же адреса /api/notifications, которым живёт колокольчик
    в портале. Своей выборки у виджета нет сознательно: иначе на вопрос
    «что нового» было бы два разных ответа.

    КАК УЗНАЁТ О НОВОМ

    Основной путь — поток событий от сервера (EventSource): о сообщении
    видно сразу, не дожидаясь опроса. Опрос по таймеру остаётся запасным
    на случай оборванного потока, и он редкий — раз в минуту.

    РАБОТАЕТ И В БРАУЗЕРЕ

    Если открыть эту страницу браузером, window.portalWidget просто нет:
    тогда она остаётся обычной страницей со списком. Проверять её так
    удобнее, чем собирая программу.
*/
(function () {
    'use strict';

    var bridge = window.portalWidget || null;

    var list = document.getElementById('wgList');
    var count = document.getElementById('wgCount');
    var tasks = document.getElementById('wgTasks');
    var check = document.getElementById('wgCheck');
    var seen = document.getElementById('wgSeen');
    var hide = document.getElementById('wgHide');
    var empty = document.getElementById('wgEmpty');
    var emptyText = document.getElementById('wgEmptyText');

    // Что человек уже видел в этом окне: по этому списку решаем,
    // о чём сообщать окошком Windows. Иначе при каждом опросе виджет
    // сообщал бы об одном и том же.
    var known = null;

    var failures = 0;

    // ------------------------------------------------------------------
    // Мелочи
    // ------------------------------------------------------------------

    function text(value) {
        return value === null || value === undefined ? '' : String(value);
    }

    /** Время события по-человечески: сегодняшнее — часами, остальное — датой. */
    function when(value) {
        var at = new Date(value);

        if (isNaN(at.getTime())) {
            return '';
        }

        var now = new Date();
        var sameDay = at.getFullYear() === now.getFullYear()
            && at.getMonth() === now.getMonth()
            && at.getDate() === now.getDate();

        if (sameDay) {
            return at.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
        }

        return at.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
    }

    function word(n, one, few, many) {
        var last = n % 10;
        var two = n % 100;

        if (two > 10 && two < 20) { return many; }
        if (last === 1) { return one; }
        if (last > 1 && last < 5) { return few; }

        return many;
    }

    // ------------------------------------------------------------------
    // Отрисовка
    // ------------------------------------------------------------------

    function draw(data) {
        var items = data.items || [];

        list.textContent = '';

        // «Нового нет» лежит готовым в разметке — его только
        // показывают и прячут.
        empty.hidden = items.length > 0;
        emptyText.textContent = 'Нового нет';

        items.forEach(function (item) {
            // Ссылка, а не кнопка: щелчок открывает «новое окно»,
            // а программа ловит это и показывает полное окно портала
            // на нужной странице. В браузере откроется вкладка.
            var row = document.createElement('a');

            row.className = 'wg-item';
            row.href = text(item.url) || '/';
            row.target = '_blank';

            var title = document.createElement('span');

            title.className = 'wg-title';
            title.textContent = text(item.title);

            row.appendChild(title);

            if (text(item.preview)) {
                var preview = document.createElement('span');

                preview.className = 'wg-prev';
                preview.textContent = item.preview;

                row.appendChild(preview);
            }

            var meta = document.createElement('span');

            meta.className = 'wg-meta';
            meta.textContent = [text(item.author), when(item.at)]
                .filter(function (part) { return part.length > 0; })
                .join(' · ');

            row.appendChild(meta);
            list.appendChild(row);
        });

        // Числа по задачам: отдельной строкой, потому что к «новому»
        // они не относятся — это работа, которая ждёт.
        chip(tasks, data.tasks);
        chip(check, data.assignedWaiting);

        seen.hidden = (data.unread || 0) === 0;
        count.hidden = (data.unread || 0) === 0;
        count.textContent = String(data.unread || 0);

        tell(data);
    }

    /**
     * Кнопка с числом справа. Подпись и значок уже лежат в разметке —
     * меняется только число, поэтому кнопка не пересобирается: иначе
     * при каждом обновлении пропадал бы наведённый вид и фокус.
     */
    function chip(element, value) {
        var n = value || 0;

        element.hidden = n === 0;

        if (n > 0) {
            element.querySelector('b').textContent = String(n);
        }
    }

    // ------------------------------------------------------------------
    // Разговор с программой
    // ------------------------------------------------------------------

    function tell(data) {
        if (!bridge) {
            return;
        }

        var unread = data.unread || 0;
        var attention = data.tasks || 0;

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

        var items = data.items || [];

        // Первый ответ после открытия окна только запоминаем: сообщать
        // окошками о том, что лежало и до запуска программы, — значит
        // засыпать человека уведомлениями при каждом входе в Windows.
        if (known === null) {
            known = {};

            items.forEach(function (item) { known[item.kind + item.id] = true; });

            return;
        }

        items.slice().reverse().forEach(function (item) {
            var key = item.kind + item.id;

            if (known[key]) {
                return;
            }

            known[key] = true;

            bridge.notify(text(item.title) || 'Портал', text(item.preview) || text(item.author));
        });
    }

    // ------------------------------------------------------------------
    // Опрос
    // ------------------------------------------------------------------

    function load() {
        return fetch('/api/notifications', {
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
        }).then(function (data) {
            failures = 0;

            draw(data);
        }).catch(function () {
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

    // Перетаскивание за заголовок: своей рамки у окна виджета нет.
    // Двигает окно сама программа — страница только сообщает о нажатии.
    var head = document.querySelector('[data-drag]');

    head.addEventListener('mousedown', function (e) {
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

    load();
    listen();

    // Запасной опрос. Редкий: основное приходит потоком.
    setInterval(load, 60000);
}());
