/*
 * Код страниц портала.
 *
 * Написан вручную, без jQuery, React и прочего: в вашей сети нет интернета,
 * а разбираться в чужой библиотеке при доработке пришлось бы всё равно.
 *
 * Разметка и приёмы взяты из присланного макета — те же имена классов,
 * те же окна, те же всплывающие сообщения. Отличие одно и оно важное:
 * в макете все данные выдуманы и лежат прямо в коде, а здесь они приходят
 * с сервера, и каждое действие сервер заново проверяет по правам.
 *
 * Файл читается сверху вниз:
 *
 *   1. Мелкие помощники
 *   2. Всплывающие сообщения
 *   3. Окна поверх страницы
 *   4. Контекстное меню
 *   5. Каркас: боковое меню, тема, клавиши, полосы заполнения
 *   6. Колокольчик уведомлений
 *   7. Файлы: выделение, открытие, действия, загрузка
 *   8. Предпросмотр
 */

(function () {
    'use strict';

    // ======================================================================
    // 1. Мелкие помощники
    // ======================================================================

    const $ = (s, root) => (root || document).querySelector(s);
    const $$ = (s, root) => [...(root || document).querySelectorAll(s)];

    /** Собирает элемент из строки разметки — как в макете. */
    const el = h => {
        const t = document.createElement('template');
        t.innerHTML = h.trim();
        return t.content.firstElementChild;
    };

    /**
     * Экранирование текста перед вставкой в разметку.
     *
     * Нужно везде, где в разметку попадает то, что ввёл человек: имя файла,
     * текст сообщения, название папки. Без этого имя вида
     * «<img onerror=...>» превратилось бы в исполняемый код.
     */
    const esc = s => String(s === null || s === undefined ? '' : s)
        .replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    /** Значок из общего набора — та же запись, что в макете. */
    const ic = (n, s = 18, w = 1.8) =>
        `<svg class="ic" width="${s}" height="${s}" viewBox="0 0 24 24" fill="none" stroke="currentColor" ` +
        `stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round"><use href="#i-${n}"/></svg>`;

    /** Человекочитаемый размер: 12,3 МБ вместо 12897485. */
    const fmtSize = b => {
        if (b < 1024) { return b + ' Б'; }
        if (b < 1048576) { return (b / 1024).toFixed(1).replace('.', ',') + ' КБ'; }
        if (b < 1073741824) { return (b / 1048576).toFixed(1).replace('.', ',') + ' МБ'; }
        return (b / 1073741824).toFixed(2).replace('.', ',') + ' ГБ';
    };

    /** «Сегодня, 14:05» вместо полной даты — так читается быстрее. */
    const relTime = iso => {
        const when = new Date(iso);

        if (isNaN(when.getTime())) { return ''; }

        const minutes = Math.floor((Date.now() - when.getTime()) / 60000);

        if (minutes < 1) { return 'только что'; }
        if (minutes < 60) { return minutes + ' мин назад'; }

        const hours = Math.floor(minutes / 60);

        if (hours < 24) { return hours + ' ч назад'; }
        if (hours < 48) { return 'вчера'; }

        return when.toLocaleDateString('ru-RU');
    };

    /**
     * Токен защиты от подделки запросов. Лежит в скрытом поле любой формы
     * на странице; без него сервер отклонит любой POST.
     */
    function token() {
        const field = $('input[name="__RequestVerificationToken"]');

        return field ? field.value : '';
    }

    /** POST на сервер с токеном. Возвращает обещание с разобранным ответом. */
    function post(url, fields) {
        const body = new FormData();

        body.append('__RequestVerificationToken', token());

        Object.keys(fields || {}).forEach(name => {
            const value = fields[name];

            (Array.isArray(value) ? value : [value]).forEach(v => body.append(name, v));
        });

        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            body
        }).then(response => (response.ok ? response : Promise.reject(response.status)));
    }

    /**
     * Отправка обычной формы: страница перезагрузится, и человек увидит
     * результат, посчитанный сервером. Так работают все изменения, которые
     * должны попасть в журнал и в права.
     */
    function submit(url, fields) {
        const form = document.createElement('form');

        form.method = 'post';
        form.action = url;
        form.hidden = true;

        const add = (name, value) => {
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            input.value = value;
            form.appendChild(input);
        };

        add('__RequestVerificationToken', token());

        Object.keys(fields || {}).forEach(name => {
            const value = fields[name];

            (Array.isArray(value) ? value : [value]).forEach(v => add(name, v));
        });

        document.body.appendChild(form);
        form.submit();
    }

    /**
     * Кладёт текст в буфер обмена и говорит об этом человеку.
     *
     * Путей два, и оба нужны. Штатный navigator.clipboard работает только
     * по защищённому соединению (HTTPS), а портал пока живёт по HTTP —
     * поэтому есть запасной: невидимое поле ввода и старая команда copy.
     * Она устарела, но работает везде и без всяких условий.
     */
    function copyText(text, okMessage) {
        const fallback = () => {
            const field = document.createElement('textarea');

            field.value = text;
            field.setAttribute('readonly', '');
            field.style.position = 'fixed';
            field.style.opacity = '0';

            document.body.appendChild(field);
            field.select();

            let ok = false;

            try { ok = document.execCommand('copy'); } catch (error) { ok = false; }

            field.remove();

            return ok;
        };

        const done = ok => toast(
            ok ? (okMessage || 'Скопировано') : 'Не удалось скопировать — возьмите адрес из строки браузера',
            ok ? 'ok' : 'warn');

        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(() => done(true)).catch(() => done(fallback()));
        } else {
            done(fallback());
        }
    }

    // ======================================================================
    // 2. Всплывающие сообщения
    //
    // Появляются справа внизу и сами исчезают. Нужны для короткого отклика
    // на действие: «скопировано», «не удалось». Для важного и необратимого
    // есть окно с подтверждением — см. дальше.
    // ======================================================================

    const TOAST_COLOR = { info: 'var(--accent)', ok: 'var(--ok)', warn: 'var(--warn)', danger: 'var(--danger)' };
    const TOAST_ICON = { info: 'info', ok: 'check', warn: 'alert', danger: 'trash' };

    function toast(msg, type, actionLabel, actionFn) {
        const kind = type || 'info';
        const root = $('#toastRoot');

        if (!root) { return null; }

        const t = el(
            `<div class="toast"><div class="t-ic t-ic--${kind}">${ic(TOAST_ICON[kind], 16, 2)}</div>` +
            `<p>${esc(msg)}</p>` +
            (actionLabel ? `<button class="t-act" type="button">${esc(actionLabel)}</button>` : '') +
            `<button class="t-x" type="button" aria-label="Закрыть">${ic('x', 14)}</button></div>`);

        root.appendChild(t);

        const kill = () => {
            t.classList.add('out');
            setTimeout(() => t.remove(), 280);
        };

        t.querySelector('.t-x').onclick = kill;

        if (actionLabel) {
            t.querySelector('.t-act').onclick = () => {
                if (actionFn) { actionFn(); }
                kill();
            };
        }

        setTimeout(kill, actionLabel ? 6000 : 4200);

        return t;
    }

    // Наружу — чтобы сервер мог показать сообщение после перезагрузки
    // страницы через data-атрибут (см. раздел 5).
    window.portalToast = toast;

    // ======================================================================
    // 3. Окна поверх страницы
    // ======================================================================

    function openModal(options) {
        closeModal();

        const wide = options.wide ? ' wide' : '';

        const overlay = el(
            `<div class="overlay"><div class="modal${wide}">` +
            `<div class="modal-head"><h3>${options.title}</h3>` +
            `<button class="icon-btn" type="button" data-mclose aria-label="Закрыть">${ic('x', 17)}</button></div>` +
            `<div class="modal-body${options.wide ? ' modal-body--wide' : ''}">${options.body}</div>` +
            (options.foot ? `<div class="modal-foot">${options.foot}</div>` : '') +
            `</div></div>`);

        $('#modalRoot').appendChild(overlay);

        // Нажатие мимо окна закрывает его. Именно mousedown, а не click:
        // иначе окно закроется, если начать выделять текст внутри него
        // и отпустить кнопку мыши снаружи.
        overlay.addEventListener('mousedown', e => {
            if (e.target === overlay) { closeModal(); }
        });

        $$('[data-mclose]', overlay).forEach(b => { b.onclick = closeModal; });

        return overlay;
    }

    function closeModal() {
        const m = $('#modalRoot .overlay');

        if (!m || m.classList.contains('closing')) { return; }

        // Окно НЕ выдёргивается разом, а уезжает вниз с затуханием —
        // тем же движением, каким появилось, только наоборот. Мгновенное
        // исчезновение особенно заметно на предпросмотре: только что был
        // документ во весь экран — и вдруг пусто, будто страница моргнула.
        m.classList.add('closing');

        // Ждём конец анимации, но не дольше: если браузер её пропустит
        // (движение выключено настройками системы), событие не придёт,
        // и окно осталось бы висеть навсегда.
        let removed = false;

        const drop = () => {
            if (removed) { return; }

            removed = true;
            m.remove();
        };

        m.addEventListener('animationend', drop);
        setTimeout(drop, 260);
    }

    /**
     * Окно подтверждения вместо системного confirm().
     * Системное выглядит по-разному в каждом браузере и выбивается
     * из оформления; здесь и вид единый, и текст можно написать нормальный.
     */
    function confirmDlg(title, text, okLabel, onOk, danger) {
        const m = openModal({
            title: esc(title),
            body: `<p class="dlg-text">${esc(text)}</p>`,
            foot: `<button class="btn" type="button" data-mclose>Отмена</button>` +
                `<button class="btn ${danger ? 'danger' : 'primary'}" type="button" id="cOk">${esc(okLabel)}</button>`
        });

        $$('[data-mclose]', m).forEach(b => { b.onclick = closeModal; });

        $('#cOk', m).onclick = () => { closeModal(); onOk(); };
    }

    /**
     * Окно «просто сообщение», с одной кнопкой.
     *
     * Отдельно от confirmDlg намеренно: там две кнопки, и вторая «Отмена»
     * рядом с сообщением, которое ничего не запускает, заставляет искать
     * разницу между ними там, где её нет.
     */
    function alertDlg(title, text, okLabel) {
        const m = openModal({
            title: esc(title),
            body: `<p class="dlg-text">${esc(text)}</p>`,
            foot: `<button class="btn primary" type="button" data-mclose>${esc(okLabel || 'Понятно')}</button>`
        });

        $$('[data-mclose]', m).forEach(b => { b.onclick = closeModal; });
    }

    /** Окно с одним полем ввода: создать папку, переименовать. */
    function promptDlg(title, label, value, okLabel, onOk) {
        const m = openModal({
            title: esc(title),
            body: `<div class="field"><label for="pIn">${esc(label)}</label>` +
                `<input type="text" id="pIn" maxlength="255" value="${esc(value)}"></div>`,
            foot: `<button class="btn" type="button" data-mclose>Отмена</button>` +
                `<button class="btn primary" type="button" id="pOk">${esc(okLabel)}</button>`
        });

        const input = $('#pIn', m);

        setTimeout(() => { input.focus(); input.select(); }, 60);

        const go = () => {
            const v = input.value.trim();

            if (v) { closeModal(); onOk(v); }
        };

        $('#pOk', m).onclick = go;
        input.onkeydown = e => { if (e.key === 'Enter') { go(); } };
    }

    // ======================================================================
    // 4. Контекстное меню (правая кнопка мыши)
    // ======================================================================

    let ctxEl = null;

    function showMenu(x, y, entries) {
        hideMenu();

        ctxEl = el(`<div class="ctx">${entries.map((e, i) => e.sep
            ? '<div class="sep"></div>'
            : `<button type="button" data-i="${i}"${e.danger ? ' class="danger"' : ''}>${ic(e.icon, 16)}${esc(e.label)}</button>`
        ).join('')}</div>`);

        document.body.appendChild(ctxEl);

        // Меню не должно вылезать за край экрана: если места справа
        // или снизу не хватает, сдвигаем его внутрь.
        const r = ctxEl.getBoundingClientRect();

        ctxEl.style.left = Math.min(x, window.innerWidth - r.width - 10) + 'px';
        ctxEl.style.top = Math.min(y, window.innerHeight - r.height - 10) + 'px';

        entries.forEach((e, i) => {
            if (e.sep) { return; }

            $(`[data-i="${i}"]`, ctxEl).onclick = () => { hideMenu(); e.fn(); };
        });
    }

    function hideMenu() {
        if (ctxEl) { ctxEl.remove(); ctxEl = null; }
    }

    document.addEventListener('click', e => { if (!e.target.closest('.ctx')) { hideMenu(); } });
    document.addEventListener('scroll', hideMenu, true);

    // ======================================================================
    // 5. Каркас: боковое меню, тема, клавиши, полосы заполнения
    // ======================================================================

    // Полосы заполнения. Ширину задаёт код, а не разметка: политика
    // безопасности страницы запрещает встроенные стили, и атрибут
    // style="width:40%" браузер молча отбросит.
    $$('[data-percent]').forEach(bar => {
        const percent = parseFloat(bar.dataset.percent);

        setTimeout(() => {
            bar.style.width = (isNaN(percent) ? 0 : Math.min(100, Math.max(0, percent))) + '%';
        }, 150);
    });

    // Боковое меню на узком экране.
    (function () {
        const burger = $('#burgerBtn');
        const sidebar = $('#sidebar');

        if (!burger || !sidebar) { return; }

        // Ширина, ниже которой меню становится выезжающим. Держится
        // в одном месте со стилями: то же число стоит в site.css.
        const NARROW = '(max-width:1100px)';

        const narrow = () => window.matchMedia(NARROW).matches;

        // Три полоски работают при ЛЮБОЙ ширине окна, а не только
        // на узком экране. На широком меню не выезжает поверх страницы,
        // а убирается совсем — рабочая область становится шире, и это
        // единственное, ради чего его там прячут. Раньше кнопка на широком
        // экране просто не показывалась, и убрать меню было нельзя.
        // На широком экране состояние меню ЗАПОМИНАЕТСЯ и применяется
        // до отрисовки следующей страницы — см. portalNav в theme.js.
        // Иначе убранное меню разворачивалось бы при каждом переходе
        // между папками и при каждой смене сортировки.
        burger.onclick = () => {
            if (narrow()) {
                sidebar.classList.toggle('open');
            } else if (window.portalNav) {
                window.portalNav.toggle();
            }
        };

        // Окно растянули на весь экран — выезжающее меню закрываем:
        // иначе оно осталось бы висеть поверх страницы уже без причины.
        window.addEventListener('resize', () => {
            if (!narrow()) { sidebar.classList.remove('open'); }
        });

        // Нажали пункт меню — оно закрывается: после перехода на другую
        // страницу открытое меню только мешает. На широком экране меню
        // не трогаем: там оно спрятано осознанно и само возвращаться
        // не должно.
        sidebar.addEventListener('click', e => {
            if (e.target.closest('.nav-item')) { sidebar.classList.remove('open'); }
        });
    })();

    // Фирменный цвет квадрата с логотипом (Branding:AccentColor).
    //
    // Цвет приходит из настроек сервера, поэтому задаётся кодом: встроенный
    // стиль style="background:…" запрещён политикой безопасности страницы.
    // Если браузер не понимает color-mix, присваивание молча не подействует
    // и останется гладиент из стилей — это допустимо, квадрат просто будет
    // прежнего цвета.
    (function () {
        const logo = $('.brand-logo');
        const color = document.body.dataset.accent;

        if (!logo || !color) { return; }

        logo.style.background =
            `linear-gradient(135deg, color-mix(in srgb, ${color} 62%, #fff), ${color})`;
    })();

    // Оформление: тема и фон рабочего пространства.
    (function () {
        const button = $('#themeBtn');
        const panel = $('#themePanel');
        const iconUse = $('#themeIconUse');

        if (!button || !window.portalTheme) { return; }

        // Запомненный фон ставится здесь, а не в theme.js: там, в <head>,
        // слоя под картинку ещё не существует.
        window.portalTheme.restoreBackground();

        // Значок на кнопке показывает, что произойдёт при смене темы:
        // на светлой — луна, на тёмной — солнце.
        const paint = () => {
            const mode = window.portalTheme.current();
            const background = window.portalTheme.background();

            iconUse.setAttribute('href', mode === 'dark' ? '#i-sun' : '#i-moon');

            if (!panel) { return; }

            $$('[data-theme-mode]', panel).forEach(row =>
                row.classList.toggle('on', row.dataset.themeMode === mode));

            $$('[data-theme-bg]', panel).forEach(row =>
                row.classList.toggle('on', row.dataset.themeBg === background));
        };

        paint();

        // Панели нет только если разметка старая — но кнопка должна
        // работать в любом случае, иначе тему станет не сменить вовсе.
        if (!panel) {
            button.onclick = () => { window.portalTheme.toggle(); paint(); };

            return;
        }

        const close = () => {
            panel.hidden = true;
            button.setAttribute('aria-expanded', 'false');
        };

        button.onclick = e => {
            e.stopPropagation();

            panel.hidden = !panel.hidden;
            button.setAttribute('aria-expanded', String(!panel.hidden));
        };

        panel.addEventListener('click', e => {
            const row = e.target.closest('[data-theme-mode],[data-theme-bg]');

            if (!row) { return; }

            if (row.dataset.themeMode) {
                window.portalTheme.set(row.dataset.themeMode);
            } else {
                window.portalTheme.setBackground(row.dataset.themeBg, row.dataset.themeUrl || '');
            }

            paint();

            // Список не закрываем: тему и фон обычно выбирают подряд,
            // и закрытие после каждого нажатия заставляло бы открывать
            // его заново.
        });

        document.addEventListener('click', e => {
            if (!panel.hidden && !e.target.closest('.theme-wrap')) { close(); }
        });

        document.addEventListener('keydown', e => {
            if (e.key === 'Escape') { close(); }
        });
    })();

    // Пасхалка: десять нажатий по своему имени в шапке открывают игру.
    //
    // Счётчик сбрасывается, если между нажатиями прошло больше двух секунд:
    // иначе десять случайных попаданий по имени за неделю однажды открыли бы
    // человеку игру посреди работы, и он бы не понял, что произошло.
    (function () {
        const chip = $('#userChip');

        if (!chip) { return; }

        let count = 0;
        let last = 0;

        chip.addEventListener('click', () => {
            const now = Date.now();

            count = now - last > 2000 ? 1 : count + 1;
            last = now;

            // Подсказка на середине пути: человек, который тыкает наугад,
            // должен понять, что что-то происходит, а не бросить на седьмом.
            if (count === 5) {
                toast('…что-то щёлкает', 'info');
            }

            if (count >= 10) {
                count = 0;

                toast('Нашли! Открываем', 'ok');

                window.open('/game.html', '_blank', 'noopener');
            }
        });
    })();

    // «Показать полностью» у длинного объявления.
    //
    // Свёрнутость задаётся классом в разметке, поэтому без JavaScript
    // объявление останется свёрнутым, но читаемым: видно начало,
    // а полный текст откроется на своей странице.
    document.addEventListener('click', e => {
        const button = e.target.closest('[data-expand]');

        if (!button) { return; }

        const body = button.previousElementSibling;

        if (!body) { return; }

        const open = body.classList.toggle('expanded');

        body.classList.toggle('clamped', !open);
        button.textContent = open ? 'Свернуть' : 'Показать полностью';
    });

    // Выделение текста в объявлении: жирным и жёлтым.
    //
    // Правым нажатием, а не панелью кнопок над полем: панель занимает место
    // постоянно, а нужна раз в десять объявлений. Родное меню браузера
    // («вставить», «проверить орфографию») при этом теряется только внутри
    // этого поля и только там, где взамен предлагается своё.
    //
    // В текст вставляются ЗНАКИ, а не разметка: в базе объявление остаётся
    // обычным текстом, который невозможно выполнить, — см. PlainTextFormatter.
    document.addEventListener('contextmenu', e => {
        const field = e.target.closest('textarea[data-format]');

        if (!field) { return; }

        e.preventDefault();

        // Границы выделения запоминаем ЗДЕСЬ, а не в обработчике пункта
        // меню: правое нажатие само по себе двигает курсор, и к моменту
        // выбора пункта выделения может уже не быть — команда тогда
        // молча не делала ничего.
        const at = { from: field.selectionStart, to: field.selectionEnd };

        const wrap = (marker, name) => {
            const from = at.from;
            const to = at.to;
            const picked = field.value.slice(from, to);

            if (!picked.trim()) {
                toast('Сначала выделите текст, потом выберите ' + name, 'warn');
                return;
            }

            // Повторный вызов на уже выделенном тексте снимает выделение:
            // иначе снять его можно было бы только правкой знаков руками.
            const already = field.value.slice(from - marker.length, from) === marker
                && field.value.slice(to, to + marker.length) === marker;

            const before = already ? field.value.slice(0, from - marker.length) : field.value.slice(0, from);
            const after = already ? field.value.slice(to + marker.length) : field.value.slice(to);

            field.value = already
                ? before + picked + after
                : before + marker + picked + marker + after;

            // Возвращаем выделение на тот же текст: с него часто сразу
            // ставят второе выделение — жирным и жёлтым вместе.
            const shift = already ? -marker.length : marker.length;

            field.focus();
            field.setSelectionRange(from + shift, to + shift);

            // Поле могло вырасти, а форма — следить за его высотой.
            field.dispatchEvent(new Event('input', { bubbles: true }));
        };

        showMenu(e.clientX, e.clientY, [
            { icon: 'edit', label: 'Жирным', fn: () => wrap('**', 'жирное') },
            { icon: 'palette', label: 'Выделить жёлтым', fn: () => wrap('==', 'выделение жёлтым') }
        ]);
    });

    // Вложения объявлений — тем же окном предпросмотра, что и файлы
    // хранилища.
    //
    // Ссылка при этом остаётся обычной ссылкой на скачивание: без
    // JavaScript вложение просто скачается, как и раньше. Перехватываем
    // только то, что портал умеет показать (data-kind не пуст).
    document.addEventListener('click', e => {
        const link = e.target.closest('[data-ann-files] [data-att]');

        if (!link || !link.dataset.kind) { return; }

        // Открыть в новой вкладке средним нажатием или с Ctrl —
        // обычное право человека, отбирать его нельзя.
        if (e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) { return; }

        e.preventDefault();

        const id = link.dataset.att;

        openPreview({
            name: link.dataset.name || 'Вложение',
            kind: link.dataset.kind,
            href: '/Announcements?handler=Attachment&fileId=' + id,
            src: '/Announcements?handler=AttachmentPreview&fileId=' + id,
            doc: '/Announcements?handler=AttachmentDocument&fileId=' + id
        }, link);
    });

    // Часы на главной.
    //
    // Время берётся у БРАУЗЕРА, а не у сервера: часы показывают время
    // того, кто на них смотрит. Сервер отрисовывает первое значение,
    // чтобы страница не мигала пустотой, дальше считает браузер.
    (function () {
        const time = $('#clockTime');
        const date = $('#clockDate');

        if (!time) { return; }

        const two = n => String(n).padStart(2, '0');

        const tick = () => {
            const now = new Date();

            time.textContent = two(now.getHours()) + ':' + two(now.getMinutes());

            if (date) {
                date.textContent = now.toLocaleDateString('ru-RU',
                    { weekday: 'long', day: 'numeric', month: 'long' });
            }
        };

        tick();

        // Раз в секунду, а не раз в минуту: иначе после открытия страницы
        // минута меняется с задержкой до минуты, и часы выглядят стоящими.
        setInterval(tick, 1000);
    })();

    // Кнопки, которые сначала спрашивают «точно?».
    //
    // Отметка ставится прямо на кнопке в разметке (data-confirm-action),
    // и без JavaScript кнопка просто отправит форму, как и раньше:
    // подтверждение — это защита от случайного нажатия, а не от злого умысла.
    document.addEventListener('click', e => {
        const button = e.target.closest('[data-confirm-action]');

        if (!button || button.dataset.confirmed === 'yes') { return; }

        const form = button.closest('form');

        if (!form) { return; }

        e.preventDefault();

        confirmDlg(
            button.dataset.confirmTitle || 'Подтвердите действие',
            button.dataset.confirmText || 'Действие нельзя отменить.',
            button.dataset.confirmOk || 'Продолжить',
            () => {
                // Метка нужна, чтобы повторное нажатие, сделанное кодом,
                // не открыло то же окно ещё раз.
                button.dataset.confirmed = 'yes';
                button.click();
            },
            true);
    });

    // Сообщение, оставленное сервером после перезагрузки страницы.
    (function () {
        const holder = $('[data-flash]');

        if (!holder) { return; }

        const text = holder.dataset.flash;

        if (text) { toast(text, holder.dataset.flashKind || 'ok'); }
    })();

    /**
     * Подтверждение перед необратимым действием.
     *
     * Достаточно повесить на кнопку отправки формы data-confirm с текстом
     * вопроса. Нужно там, где отменить уже нельзя: очистка корзины,
     * стирание файла, очистка журнала.
     */
    document.addEventListener('click', e => {
        const button = e.target.closest('[data-confirm]');

        if (!button || button.dataset.confirmed === '1') { return; }

        e.preventDefault();

        confirmDlg(
            button.dataset.confirmTitle || 'Подтвердите действие',
            button.dataset.confirm,
            button.dataset.confirmOk || 'Выполнить',
            () => {
                // Второй раз спрашивать не надо: помечаем кнопку
                // и нажимаем её заново, теперь уже по-настоящему.
                button.dataset.confirmed = '1';
                button.click();
            },
            true);
    }, true);

    // Клавиши — те же, что в макете.
    document.addEventListener('keydown', e => {
        // Ctrl+K переводит курсор в поиск, где бы человек ни находился.
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
            const search = $('#globalSearch');

            if (search) { e.preventDefault(); search.focus(); search.select(); }

            return;
        }

        if (e.key === 'Escape') {
            hideMenu();
            closeModal();

            const panel = $('#notifPanel');

            if (panel) { panel.hidden = true; }

            const sidebar = $('#sidebar');

            if (sidebar) { sidebar.classList.remove('open'); }
        }
    });

    // ======================================================================
    // 6. Колокольчик уведомлений
    //
    // Каждые несколько секунд спрашиваем сервер, нет ли нового. Постоянного
    // соединения нет намеренно: между офисами канал с урезанным MTU,
    // и рвущийся WebSocket выглядел бы как «портал завис».
    //
    // Раньше спрашивали раз в минуту, и о новом сообщении человек узнавал
    // с опозданием до минуты — на практике это значило «узнавал, обновив
    // страницу». Теперь запрос идёт раз в семь секунд, но ТОЛЬКО пока
    // вкладка открыта и видна: свёрнутое окно портала не спрашивает ничего,
    // и десяток забытых вкладок сервер не нагружает. Ответ короткий —
    // несколько десятков байт, когда нового нет.
    // ======================================================================

    (function () {
        const bell = $('#bellBtn');
        const badge = $('#bellBadge');
        const panel = $('#notifPanel');

        if (!bell || !panel) { return; }

        /** Как часто спрашиваем. Семь секунд — «сразу» на глаз, но не поток запросов. */
        const POLL_MS = 7000;

        // Что уже показывали всплывающим сообщением — чтобы не показывать
        // одно и то же каждую минуту. Живёт до перезагрузки страницы.
        const announced = new Set();
        let first = true;
        let timer = null;
        let toldAboutSignOut = false;

        const NOTIF_ICON = { announcement: 'mega', message: 'chat' };

        function setCount(id, value) {
            const element = $(id);

            if (!element) { return; }

            element.hidden = !value;
            element.textContent = value > 99 ? '99+' : String(value);
        }

        function render(data) {
            const unread = data.unread || 0;

            badge.hidden = unread === 0;
            badge.textContent = unread > 99 ? '99+' : String(unread);

            setCount('#chatBadge', data.messages || 0);
            setCount('#annBadge', data.announcements || 0);

            const items = data.items || [];

            panel.innerHTML =
                '<div class="notif-head"><b>Уведомления</b>' +
                '<button type="button" id="readAllBtn">Прочитать все</button></div>' +
                '<div class="notif-scroll">' +
                (items.length
                    ? items.map(n => {
                        const kind = n.kind === 'message' ? 'message' : 'announcement';

                        // Ссылка, а не кнопка: нажатие ведёт к самому событию —
                        // к объявлению или к переписке. Всё непрочитанное
                        // при этом отмечается прочитанным, иначе счётчик
                        // на колокольчике висел бы до отдельного нажатия.
                        return `<a class="notif" href="${esc(n.url)}" data-seen>` +
                            `<div class="n-ic n-ic--${kind}">${ic(NOTIF_ICON[kind], 17)}</div>` +
                            `<p><b>${esc(n.title)}</b>` +
                            `<time>${esc(n.author)} · ${esc(relTime(n.at))}</time></p>` +
                            '<div class="ndot"></div></a>';
                    }).join('')
                    : '<div class="empty notif-empty"><p>Нет уведомлений</p></div>') +
                '</div>';

            // Переход по уведомлению = «я это видел».
            $$('[data-seen]', panel).forEach(link => {
                link.addEventListener('click', () => {
                    // Ждать ответа не нужно: человек уже уходит на страницу,
                    // а отметка успеет дойти до сервера сама.
                    post('/api/notifications/seen', {}).catch(() => { /* не страшно */ });
                });
            });

            $('#readAllBtn', panel).onclick = e => {
                e.stopPropagation();

                post('/api/notifications/seen', {})
                    .then(() => { panel.hidden = true; poll(); })
                    .catch(() => toast('Не удалось отметить прочитанным', 'warn'));
            };

            // При первой проверке всплывающих сообщений не показываем:
            // человек только что открыл страницу, и непрочитанное для него
            // не новость, а просто счётчик на колокольчике.
            if (first) {
                items.forEach(n => announced.add(n.kind + ':' + n.id));
                first = false;
                return;
            }

            items.forEach(n => {
                const key = n.kind + ':' + n.id;

                if (announced.has(key)) { return; }

                announced.add(key);

                // Про беседу, которая сейчас открыта, не сообщаем: человек
                // смотрит прямо на неё, сообщение он уже видит в ленте,
                // а всплывающее окошко поверх — только помеха.
                if (n.url && location.pathname + location.search === n.url) { return; }

                toast((n.kind === 'message' ? 'Новое сообщение: ' : 'Новое объявление: ') + n.title, 'info');
            });
        }

        function poll() {
            // На скрытой вкладке не спрашиваем: человек её не видит,
            // а вернувшись, получит проверку сразу (см. visibilitychange).
            if (document.visibilityState === 'hidden') { return; }

            fetch('/api/notifications', {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                .then(render)
                .catch(reason => {
                    // Истёкший вход — не «связь моргнула». Молчать про него
                    // нельзя: страница выглядит рабочей, а на деле не работает
                    // уже ничего. Говорим один раз и перестаём спрашивать.
                    if (reason === 401) {
                        if (timer !== null) { clearInterval(timer); timer = null; }

                        badge.hidden = true;

                        if (!toldAboutSignOut) {
                            toldAboutSignOut = true;
                            toast('Вход в портал истёк. Обновите страницу и войдите заново.', 'danger');
                        }
                    }

                    // Всё остальное — молча: связь могла моргнуть, а ругаться
                    // на это раз в минуту было бы издевательством.
                });
        }

        bell.onclick = e => {
            e.stopPropagation();
            panel.hidden = !panel.hidden;
        };

        document.addEventListener('click', e => {
            if (!panel.hidden && !e.target.closest('.notif-wrap')) { panel.hidden = true; }
        });

        poll();
        timer = setInterval(poll, POLL_MS);

        // Вернулись на вкладку — проверяем сразу, не дожидаясь минуты.
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible' && timer !== null) { poll(); }
        });
    })();

    // ======================================================================
    // 7. Файлы
    // ======================================================================

    const files = $('#filesArea');

    if (files) {
        const container = $('#filesContainer');
        const settings = files.dataset;

        // ВСЯ рабочая область страницы: и список, и пустота под ним.
        // По ней работают и контекстное меню, и обводка рамкой — человек
        // целится в «пустое место страницы», и разницу между «пусто внутри
        // списка» и «пусто под списком» видит только разметка.
        const area = files.closest('.view') || files;

        const folderId = settings.folderId || '';
        const canWrite = settings.canWrite === 'true';
        const canCreateFolder = settings.canCreateFolder === 'true';

        let selected = null;

        /** Данные выделенного объекта из его разметки. */
        const info = node => ({
            id: node.dataset.id,
            file: node.dataset.file || '',
            folder: node.dataset.folder || '',
            name: node.dataset.name || '',
            href: node.dataset.href || '',
            kind: node.dataset.kind || '',
            fav: node.dataset.fav === 'true',
            pinned: node.dataset.pinned === 'true',
            canPin: node.dataset.canPin === 'true',
            canDelete: node.dataset.canDelete === 'true',
            canManage: node.dataset.canManage === 'true'
        });

        function select(node) {
            $$('.selected', container).forEach(x => x.classList.remove('selected'));

            selected = node || null;

            if (node) { node.classList.add('selected'); }
        }

        // ---------- Выделение нескольких объектов ----------
        //
        // Как в проводнике: обычное нажатие выделяет один, Ctrl добавляет
        // и убирает по одному, Shift берёт всё подряд от прошлого нажатия,
        // а протягивание мышью по пустому месту обводит рамкой.
        //
        // Хранится не список узлов, а сама разметка с классом .selected:
        // страница перерисовывается целиком при любом действии, и список
        // узлов после этого указывал бы в никуда.

        const picked = () => $$('.selected', container);

        function marked(node, on) {
            node.classList.toggle('selected', on);
        }

        /** Все плитки/строки ВИДИМОГО сейчас вида — по ним считается Shift. */
        function visibleNodes() {
            return $$('[data-id]', container).filter(n => n.offsetParent !== null);
        }

        function selectRange(fromNode, toNode) {
            const all = visibleNodes();
            const a = all.indexOf(fromNode);
            const b = all.indexOf(toNode);

            if (a < 0 || b < 0) { return; }

            const [start, end] = a <= b ? [a, b] : [b, a];

            all.forEach((n, i) => marked(n, i >= start && i <= end));
        }

        container.addEventListener('click', e => {
            if (e.target.closest('.fav-star')) { return; }

            const node = e.target.closest('[data-id]');

            if (!node) {
                select(null);
                return;
            }

            if (e.shiftKey && selected) {
                // Обычный выбор диапазона сбрасывает выделение текста,
                // которое браузер делает при Shift-нажатии.
                window.getSelection()?.removeAllRanges();
                selectRange(selected, node);

                return;
            }

            if (e.ctrlKey || e.metaKey) {
                marked(node, !node.classList.contains('selected'));
                selected = node;

                return;
            }

            select(node);
        });

        // ---------- Обводка рамкой ----------
        (function () {
            let box = null;
            let startX = 0;
            let startY = 0;
            let additive = false;

            // Слушаем ВСЮ рабочую область страницы, а не только список
            // и даже не только поле файлов: обводить начинают и правее
            // последней плитки, и далеко под ней — там, где список давно
            // кончился, а страница ещё нет.
            area.addEventListener('mousedown', e => {
                // Только левой кнопкой и только по пустому месту: начав
                // с плитки, человек её перетаскивает или открывает.
                if (e.button !== 0 || e.target.closest('[data-id]')) { return; }

                // Управляющие элементы верхней панели — не «пустое место».
                // Обводка начинается с preventDefault, а список «Порядок»
                // раскрывается именно по нажатию кнопки: запрет действия
                // по умолчанию не давал ему открыться вовсе — сортировка
                // выглядела сломанной, хотя обработчик исправно висел.
                if (e.target.closest('select, input, textarea, button, a, label, .toolbar')) {
                    return;
                }

                additive = e.ctrlKey || e.metaKey;

                // Координаты ОКНА, а не документа: прокручивается рабочая
                // область, а не страница, и в координатах документа рамка
                // разъезжалась бы с плитками (см. .marquee в стилях).
                startX = e.clientX;
                startY = e.clientY;

                box = el('<div class="marquee"></div>');
                document.body.appendChild(box);
                files.classList.add('marking');

                if (!additive) { select(null); }

                e.preventDefault();
            });

            document.addEventListener('mousemove', e => {
                if (!box) { return; }

                const left = Math.min(startX, e.clientX);
                const top = Math.min(startY, e.clientY);
                const width = Math.abs(e.clientX - startX);
                const height = Math.abs(e.clientY - startY);

                // Размеры задаёт код, а не разметка: встроенные стили
                // запрещены политикой безопасности страницы.
                box.style.left = left + 'px';
                box.style.top = top + 'px';
                box.style.width = width + 'px';
                box.style.height = height + 'px';

                // getBoundingClientRect тоже отдаёт координаты окна,
                // поэтому пересчитывать ничего не нужно.
                const rect = { left, top, right: left + width, bottom: top + height };

                visibleNodes().forEach(node => {
                    const b = node.getBoundingClientRect();

                    // Достаточно ЛЮБОГО пересечения, даже на пиксель: человек
                    // ведёт рамку по краям плиток и ждёт, что задетое
                    // выделится. Требование «накрыть целиком» заставляло бы
                    // обводить с запасом и промахиваться.
                    const hit = b.right > rect.left && b.left < rect.right
                        && b.bottom > rect.top && b.top < rect.bottom;

                    // С Ctrl рамка ДОБАВЛЯЕТ к уже выделенному, поэтому
                    // не попавшие в неё не трогаем.
                    if (hit || !additive) { marked(node, hit || (additive && node.classList.contains('selected'))); }
                });
            });

            document.addEventListener('mouseup', () => {
                if (!box) { return; }

                box.remove();
                box = null;
                files.classList.remove('marking');

                const list = picked();

                selected = list.length ? list[list.length - 1] : null;
            });
        })();

        // ---------- Перетаскивание в другую папку ----------
        //
        // Тащить можно и файл, и папку; бросать — на плитку папки в списке,
        // на любой шаг пути наверху (в том числе на «Файлы» — это верхний
        // уровень) и на кнопку «Назад», то есть в родительскую папку.
        //
        // Перетаскивается ВСЁ ВЫДЕЛЕННОЕ, а не только та плитка, за которую
        // взялись: человек выделил десяток файлов рамкой и тянет их вместе.
        // Если взялись за невыделенное — тащим только его, и выделение
        // переезжает на него же, иначе на экране выделено одно, а едет другое.
        //
        // Права здесь не проверяются намеренно: их проверяет сервер, по обеим
        // папкам сразу. Запрещать перетаскивание в браузере значило бы
        // повторять те же правила во втором месте — и однажды разойтись с ними.
        (function () {
            let dragging = [];

            const clear = () => $$('.drop-hot').forEach(n => n.classList.remove('drop-hot'));

            /** Куда можно бросить: папка в списке, шаг пути, кнопка «Назад». */
            const targetOf = node => {
                if (!node || !node.closest) { return null; }

                const tile = node.closest('[data-folder]');

                if (tile && tile.dataset.folder && !tile.classList.contains('dragged')) {
                    return { node: tile, id: tile.dataset.folder };
                }

                const crumb = node.closest('.crumb');

                if (crumb) {
                    // У корневого шага пути номера нет — это верхний уровень,
                    // и сервер понимает его как 0.
                    const match = /[?&]id=(\d+)/.exec(crumb.getAttribute('href') || '');

                    return { node: crumb, id: match ? match[1] : '0' };
                }

                const back = node.closest('.back-btn');

                if (back && back.getAttribute('href')) {
                    const match = /[?&]id=(\d+)/.exec(back.getAttribute('href'));

                    return { node: back, id: match ? match[1] : '0' };
                }

                return null;
            };

            container.addEventListener('dragstart', e => {
                const node = e.target.closest('[data-id]');

                if (!node) { return; }

                if (!node.classList.contains('selected')) { select(node); }

                dragging = picked().length ? picked() : [node];
                dragging.forEach(n => n.classList.add('dragged'));

                // Данные в обмене нужны, иначе Firefox не начинает
                // перетаскивание вовсе. Само значение мы не читаем:
                // список лежит в dragging.
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData('text/plain', dragging.map(n => n.dataset.name).join('\n'));
            });

            document.addEventListener('dragend', () => {
                $$('.dragged').forEach(n => n.classList.remove('dragged'));
                clear();
                dragging = [];
            });

            document.addEventListener('dragover', e => {
                if (!dragging.length) { return; }

                const target = targetOf(e.target);

                if (!target) { clear(); return; }

                e.preventDefault();
                e.dataTransfer.dropEffect = 'move';

                if (!target.node.classList.contains('drop-hot')) {
                    clear();
                    target.node.classList.add('drop-hot');
                }
            });

            document.addEventListener('drop', e => {
                if (!dragging.length) { return; }

                const target = targetOf(e.target);

                if (!target) { return; }

                e.preventDefault();

                const items = dragging.map(info);
                const fileList = items.filter(i => i.file).map(i => i.file);
                const folderList = items.filter(i => i.folder).map(i => i.folder);

                clear();
                $$('.dragged').forEach(n => n.classList.remove('dragged'));
                dragging = [];

                // Бросили туда же, где и лежало, — делать нечего.
                if (target.id === (folderId || '0')) { return; }

                const what = items.length === 1
                    ? `«${items[0].name}»`
                    : `выбранное (${items.length})`;

                confirmDlg('Переместить?',
                    `Переместить ${what} в другую папку? Ссылки на перемещённое останутся рабочими.`,
                    'Переместить',
                    () => submit('/Files?handler=Move', {
                        targetFolderId: target.id,
                        fileIds: fileList,
                        folderIds: folderList
                    }));
            });
        })();

        container.addEventListener('dblclick', e => {
            const node = e.target.closest('[data-id]');

            if (node) { open(info(node), node); }
        });

        area.addEventListener('contextmenu', e => {
            // Поля ввода и уже выделенный текст оставляем браузеру: там его
            // меню и нужно — «вставить», «проверить орфографию», «копировать».
            if (e.target.closest('input, textarea, select')) {
                return;
            }

            const selection = window.getSelection();

            if (selection && !selection.isCollapsed && e.target.closest('a, p, .search-note')) {
                return;
            }

            const node = e.target.closest('[data-id]');

            e.preventDefault();

            if (node) {
                // Нажали по объекту ВНУТРИ выделенного — выделение сохраняем
                // и показываем меню для всей группы. Так работает проводник,
                // и иначе право нажатие сбрасывало бы то, что человек
                // только что набрал мышью.
                if (node.classList.contains('selected') && picked().length > 1) {
                    groupMenu(picked(), e.clientX, e.clientY);

                    return;
                }

                select(node);
                itemMenu(info(node), e.clientX, e.clientY);
            } else {
                select(null);
                areaMenu(e.clientX, e.clientY);
            }
        });

        /**
         * Меню для нескольких выделенных объектов.
         *
         * Здесь только то, что осмысленно делать пачкой. «Переименовать»
         * или «свойства» для десяти объектов сразу смысла не имеют,
         * и их тут нет.
         */
        function groupMenu(nodes, x, y) {
            const items = nodes.map(info);
            const fileList = items.filter(i => i.file).map(i => i.file);
            const folderList = items.filter(i => i.folder).map(i => i.folder);

            const entries = [];

            entries.push({
                icon: 'download',
                label: `Скачать архивом (${items.length})`,
                fn: () => {
                    toast('Готовим архив, скачивание начнётся само', 'info');

                    const query = [];

                    if (fileList.length) { query.push('fileIds=' + fileList.join(',')); }
                    if (folderList.length) { query.push('folderIds=' + folderList.join(',')); }
                    if (folderId) { query.push('folderId=' + folderId); }

                    window.location.href = '/Files?handler=DownloadZip&' + query.join('&');
                }
            });

            entries.push({ sep: 1 });
            clipEntries(items).forEach(entry => entries.push(entry));

            // В корзину — только файлы и только те, которые человеку
            // разрешено удалять. Папки удаляются по одной и только пустые:
            // пакетное удаление папок слишком легко сделать не глядя.
            const deletable = items.filter(i => i.file && i.canDelete);

            if (deletable.length) {
                entries.push({ sep: 1 });
                entries.push({
                    icon: 'trash',
                    label: `В корзину (${deletable.length})`,
                    danger: 1,
                    fn: () => removeMany(items)
                });
            }

            showMenu(x, y, entries);
        }

        /**
         * Убрать в корзину всё выделенное.
         *
         * Отдельно от меню, потому что вызывается ещё и клавишей Delete:
         * выделив десяток файлов рамкой, человек жмёт Delete и ждёт, что
         * уйдут все десять, а не последний нажатый.
         *
         * Папки в пачку не входят: удалять их можно только пустыми и по
         * одной — слишком легко снести не глядя целый раздел. О пропущенных
         * говорим прямо, чтобы «удалил, а папка осталась» не выглядело сбоем.
         */
        function removeMany(items) {
            const deletable = items.filter(i => i.file && i.canDelete).map(i => i.file);
            const folders = items.filter(i => i.folder).length;
            const refused = items.filter(i => i.file && !i.canDelete).length;

            if (!deletable.length) {
                toast(folders
                    ? 'Папки удаляются по одной — правым нажатием на папке'
                    : 'Удалять эти файлы вам нельзя', 'warn');

                return;
            }

            const notes = [];

            if (folders) { notes.push(`папок пропущено: ${folders} — их удаляют по одной`); }
            if (refused) { notes.push(`без прав на удаление: ${refused}`); }

            confirmDlg('Переместить в корзину?',
                `Файлов: ${deletable.length}. Восстановить их можно будет из раздела «Корзина».`
                    + (notes.length ? '\n\n' + notes.join('; ') : ''),
                'В корзину',
                () => submit('/Files?handler=DeleteFiles' + (folderId ? '&folderId=' + folderId : ''),
                    { fileIds: deletable }),
                true);
        }

        // ---------- Свой буфер обмена ----------
        //
        // Держится в sessionStorage, а не в переменной: копируют в одной
        // папке, вставляют в другой — между этими двумя действиями страница
        // перезагружается, и переменная не пережила бы перехода. И не
        // в localStorage: буфер живёт до закрытия вкладки, а не вечно,
        // иначе «вставить» предлагалось бы и назавтра.
        //
        // Буфер портала НЕ связан с буфером обмена Windows: положить сюда
        // файл из проводника нельзя, и наоборот. Файлы из проводника
        // по-прежнему просто перетаскивают в окно или вставляют по Ctrl+V —
        // это отдельная дорога, и путать их не нужно.
        const CLIP_KEY = 'portal.clip';

        function clipRead() {
            try {
                const raw = sessionStorage.getItem(CLIP_KEY);

                return raw ? JSON.parse(raw) : null;
            } catch (error) {
                return null;
            }
        }

        function clipWrite(value) {
            try {
                if (value) {
                    sessionStorage.setItem(CLIP_KEY, JSON.stringify(value));
                } else {
                    sessionStorage.removeItem(CLIP_KEY);
                }
            } catch (error) {
                toast('Браузер не дал запомнить выбранное', 'warn');
            }
        }

        /** Положить выделенное в буфер: mode — copy или cut. */
        function clipPut(items, mode) {
            const value = {
                mode,
                files: items.filter(i => i.file).map(i => i.file),
                folders: items.filter(i => i.folder).map(i => i.folder),
                count: items.length,
                name: items.length === 1 ? items[0].name : ''
            };

            clipWrite(value);

            const what = value.name ? `«${value.name}»` : `выбрано: ${value.count}`;

            toast((mode === 'cut' ? 'Вырезано — ' : 'Скопировано — ') + what
                + '. Откройте нужную папку и нажмите «Вставить».', 'info');
        }

        function clipPaste() {
            const clip = clipRead();

            if (!clip || !folderId) { return; }

            // Вырезанное ПЕРЕМЕЩАЕТСЯ тем же обработчиком, что и
            // перетаскивание: это одно и то же действие, и второй
            // его разновидности на сервере быть не должно.
            submit(clip.mode === 'cut' ? '/Files?handler=Move' : '/Files?handler=Copy', {
                targetFolderId: folderId,
                fileIds: clip.files,
                folderIds: clip.folders
            });

            // Вырезанное из буфера уходит: вставить его второй раз
            // означало бы перенести уже перенесённое.
            if (clip.mode === 'cut') { clipWrite(null); }
        }

        /** Пункты «копировать» и «вырезать» — одинаковые для одного объекта и для группы. */
        function clipEntries(items) {
            return [
                { icon: 'copy', label: 'Копировать', fn: () => clipPut(items, 'copy') },
                { icon: 'cut', label: 'Вырезать', fn: () => clipPut(items, 'cut') }
            ];
        }

        function open(item, node) {
            if (item.folder) {
                window.location.href = item.href;
                return;
            }

            // Показать умеем не всё. Что не умеем — просто скачиваем:
            // «открыть» для такого файла и есть «скачать».
            if (item.kind) {
                openPreview(item, node);
            } else {
                window.location.href = item.href;
            }
        }

        function itemMenu(item, x, y) {
            const entries = [];

            entries.push(item.folder
                ? { icon: 'folder', label: 'Открыть', fn: () => open(item, null) }
                : { icon: 'eye', label: 'Предпросмотр', fn: () => open(item, $(`[data-id="${item.id}"]`, container)) });

            if (item.file) {
                entries.push({ icon: 'download', label: 'Скачать', fn: () => { window.location.href = item.href; } });
            }

            // Ускоренное скачивание. Портал сжимает выбранное в архив и отдаёт
            // одним потоком: документы ужимаются в разы, а папка приходит
            // одним файлом вместо десятков отдельных скачиваний.
            //
            // Адрес открывается сменой location, а не fetch: скачивание должен
            // вести сам браузер — со своей полосой в списке загрузок, паузой
            // и возобновлением. Забрать архив в память страницы значило бы
            // держать в ней целую папку.
            entries.push({
                icon: 'download',
                label: item.folder ? 'Скачать папку архивом' : 'Скачать архивом (быстрее)',
                fn: () => {
                    toast('Готовим архив, скачивание начнётся само', 'info');

                    window.location.href = '/Files?handler=DownloadZip&'
                        + (item.file ? 'fileId=' + item.file : 'folderId=' + item.folder);
                }
            });

            entries.push({
                icon: 'star',
                label: item.fav ? 'Убрать из избранного' : 'В избранное',
                fn: () => toggleFav(item)
            });

            if (item.canPin) {
                entries.push({
                    icon: 'pin',
                    label: item.pinned ? 'Открепить' : 'Закрепить наверху',
                    fn: () => togglePin(item)
                });
            }

            entries.push({ sep: 1 });
            clipEntries([item]).forEach(entry => entries.push(entry));

            if (folderId && clipRead()) {
                entries.push({
                    icon: 'paste',
                    label: pasteLabel(),
                    fn: clipPaste
                });
            }

            entries.push({ sep: 1 });
            entries.push({ icon: 'share', label: 'Скопировать ссылку', fn: () => copyLink(item) });

            if (item.file && item.canDelete) {
                entries.push({ icon: 'edit', label: 'Переименовать', fn: () => rename(item) });
            }

            if (item.folder && item.canManage) {
                entries.push({ icon: 'edit', label: 'Переименовать', fn: () => rename(item) });
                entries.push({ icon: 'info', label: 'Управление папкой', fn: () => folderSettings(item.folder) });
            }

            entries.push({ icon: 'info', label: 'Свойства', fn: () => properties(item) });

            if ((item.file && item.canDelete) || (item.folder && item.canManage)) {
                entries.push({ sep: 1 });
                entries.push({ icon: 'trash', label: 'Удалить', danger: 1, fn: () => remove(item) });
            }

            showMenu(x, y, entries);
        }

        function areaMenu(x, y) {
            const entries = [];

            if (canCreateFolder) {
                entries.push({ icon: 'plus', label: 'Создать папку', fn: newFolder });
            }

            if (canWrite) {
                entries.push({ icon: 'upload', label: 'Загрузить файлы', fn: () => $('#fileInput').click() });
            }

            // «Вставить» показывается, только когда есть что вставлять
            // и есть куда: в корне хранилища файл лежать не может.
            if (canWrite && folderId && clipRead()) {
                entries.push({ icon: 'paste', label: pasteLabel(), fn: clipPaste });
            }

            if (folderId) {
                entries.push({
                    icon: 'download',
                    label: 'Скачать эту папку архивом',
                    fn: () => {
                        toast('Готовим архив, скачивание начнётся само', 'info');

                        window.location.href = '/Files?handler=DownloadZip&folderId=' + folderId;
                    }
                });
            }

            entries.push({ sep: 1 });
            entries.push({ icon: 'restore', label: 'Обновить', fn: () => window.location.reload() });

            showMenu(x, y, entries);
        }

        /** Подпись «вставить»: человек должен видеть, что именно вставит. */
        function pasteLabel() {
            const clip = clipRead();

            if (!clip) { return 'Вставить'; }

            const what = clip.name ? `«${clip.name}»` : `${clip.count} шт.`;

            return (clip.mode === 'cut' ? 'Вставить (перенести) ' : 'Вставить ') + what;
        }

        function toggleFav(item) {
            const query = item.file ? 'fileId=' + item.file : 'folderId=' + item.folder;

            post('/Files?handler=Favorite&' + query, {})
                .then(r => r.json())
                .then(data => {
                    const node = $(`[data-id="${item.id}"]`, container);

                    if (node) { node.dataset.fav = String(data.favorite); }

                    const star = node && $('.fav-star', node);

                    if (star) { star.classList.toggle('on', data.favorite); }

                    toast(data.favorite ? 'Добавлено в избранное' : 'Убрано из избранного', 'info');
                })
                .catch(() => toast('Не удалось изменить избранное', 'danger'));
        }

        /**
         * Закрепить или открепить.
         *
         * После ответа страница перечитывается целиком, в отличие
         * от звёздочки: закрепление МЕНЯЕТ ПОРЯДОК списка, и оставить
         * плитку на прежнем месте значило бы соврать — при следующем
         * заходе она окажется в другом.
         */
        function togglePin(item) {
            const query = item.file ? 'fileId=' + item.file : 'folderId=' + item.folder;

            post('/Files?handler=Pin&' + query, {})
                .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                .then(data => {
                    toast(data.pinned ? 'Закреплено наверху' : 'Откреплено', 'info');

                    window.location.reload();
                })
                .catch(reason => toast(
                    reason === 403
                        ? 'Открепить может только тот, кто закрепил, или администратор'
                        : 'Не удалось изменить закрепление',
                    'warn'));
        }

        function copyLink(item) {
            // Ссылка ОБЫЧНАЯ, не «публичная»: анонимного доступа портал
            // не даёт вовсе. Тот, кому её пошлют, откроет файл или папку,
            // только если у него и так есть к ней доступ.
            copyText(
                location.origin + (item.folder ? '/Files?id=' + item.folder : item.href),
                item.folder ? 'Ссылка на папку скопирована' : 'Ссылка на файл скопирована');
        }

        function rename(item) {
            promptDlg('Переименовать', 'Новое название', item.name, 'Сохранить', value => {
                submit(item.file
                    ? '/Files?handler=RenameFile'
                    : '/Files?handler=RenameFolder',
                    item.file
                        ? { fileId: item.file, newName: value }
                        : { folderId: item.folder, newName: value });
            });
        }

        function newFolder() {
            promptDlg('Создать папку', 'Название папки', 'Новая папка', 'Создать', value => {
                const url = folderId
                    ? '/Files?handler=CreateFolder&parentId=' + folderId
                    : '/Files?handler=CreateFolder';

                submit(url, { NewFolderName: value });
            });
        }

        function remove(item) {
            if (item.folder) {
                confirmDlg('Удалить папку?',
                    `Папка «${item.name}» будет удалена. Удалить можно только пустую папку.`,
                    'Удалить',
                    () => submit('/Files?handler=DeleteFolder', { folderId: item.folder }),
                    true);

                return;
            }

            confirmDlg('Переместить в корзину?',
                `Файл «${item.name}» отправится в корзину. Восстановить его можно будет из раздела «Корзина».`,
                'В корзину',
                () => submit('/Files?handler=DeleteFiles' + (folderId ? '&folderId=' + folderId : ''),
                    { fileIds: item.file }),
                true);
        }

        /**
         * Управление папкой ОКНОМ поверх списка, а не отдельной страницей.
         *
         * Настройки правят, стоя в папке и глядя на её содержимое. Уход
         * на отдельную страницу означал потерю места: вернувшись, человек
         * оказывался в начале списка и заново искал, где был.
         *
         * Сохраняют изменения обработчики САМОЙ страницы настроек —
         * обычной отправкой формы, как и всё остальное в портале. Правила
         * проверки при этом живут в одном месте, а страница остаётся
         * рабочей и без JavaScript. Признак returnTo=files говорит серверу
         * вернуть человека в папку, а не на страницу настроек.
         */
        function folderSettings(id) {
            fetch('/Files?handler=FolderSettings&folderId=' + id, {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                .then(data => showFolderSettings(data))
                .catch(reason => toast(
                    reason === 404
                        ? 'Управлять этой папкой можно только с правом «Управление»'
                        : 'Не удалось получить настройки папки',
                    'warn'));
        }

        function showFolderSettings(data) {
            const save = '/Files/Settings/' + data.id + '?handler=Save&returnTo=files';
            const add = '/Files/Settings/' + data.id + '?handler=AddPermission&returnTo=files';

            const levels = [
                ['Read', 'Чтение — видеть и скачивать'],
                ['Write', 'Запись — загружать и создавать подпапки'],
                ['Manage', 'Управление — удалять и менять права']
            ];

            const permRows = list => (list || []).map(p =>
                `<div class="perm-row" data-perm="${p.id}">` +
                `<span class="perm-kind">${p.isUser ? 'сотрудник' : 'группа'}</span>` +
                `<code>${esc(p.groupName)}</code>` +
                `<span class="perm-level">${esc(levelName(p.access))}</span>` +
                `<button class="link danger" type="button" data-drop-perm="${p.id}">Убрать</button>` +
                '</div>').join('')
                || '<p class="hint">Своих прав у папки пока нет.</p>';

            const rows = '<div id="fsPerms">' + permRows(data.permissions) + '</div>';

            const m = openModal({
                wide: true,
                title: `Управление папкой «${esc(data.name)}»`,
                body:
                    `<p class="hint fs-path">${esc(data.path)}</p>` +

                    '<div class="fs-grid">' +

                    '<section class="fs-block"><h4>Права доступа</h4>' +

                    '<label class="fs-check"><input type="checkbox" id="fsInherit"' +
                    (data.inherit ? ' checked' : '') + ' />' +
                    '<span>Наследовать права родительской папки</span></label>' +
                    '<p class="hint">Выключено — действуют только права, назначенные здесь: ' +
                    'папка становится закрытой даже для тех, у кого есть доступ выше.</p>' +

                    rows +

                    '<div class="fs-add">' +
                    '<input type="text" id="fsGroup" maxlength="256" autocomplete="off"' +
                    ' placeholder="Группа или сотрудник — нажмите, чтобы выбрать" />' +
                    '<select id="fsLevel">' +
                    levels.map(l => `<option value="${l[0]}">${esc(l[1])}</option>`).join('') +
                    '</select>' +
                    '<button class="btn" type="button" id="fsAdd">Выдать доступ</button>' +
                    '<div class="dir-tree" id="fsTree" hidden></div>' +
                    '</div>' +
                    '<p class="hint">Если группа или сотрудник уже в списке, уровень заменится ' +
                    'на выбранный. Права складываются: действует наибольший.</p>' +

                    '</section>' +

                    // Пределы и автоочистка — дело администратора портала:
                    // они про место на диске и про то, что общего у всех папок.
                    // Хозяину своей папки нужны ПРАВА — кому её показать;
                    // остальное только загромождало бы окно вопросами,
                    // на которые он всё равно не отвечает.
                    (!data.isAdmin ? '' :
                    '<section class="fs-block"><h4>Ограничения и хранение</h4>' +

                    '<div class="field"><label for="fsMax">Предел размера одного файла, МБ</label>' +
                    `<input type="number" id="fsMax" min="0" value="${data.maxFileSizeMb ?? ''}" placeholder="как у родителя" />` +
                    `<p class="hint">Сейчас действует: <b>${esc(data.effectiveMax)}</b>. ` +
                    `Пусто — значение родителя, а если и там пусто — общее (${data.defaultMaxMb} МБ). <b>0 — без ограничения.</b>` +
                    (data.absoluteMaxMb > 0 ? ` Выше общего предела портала (${data.absoluteMaxMb} МБ) поставить нельзя.` : '') +
                    '</p></div>' +

                    '<div class="field"><label for="fsQuota">Квота папки, МБ</label>' +
                    `<input type="number" id="fsQuota" min="0" value="${data.quotaMb ?? ''}" placeholder="как у родителя" />` +
                    `<p class="hint">Сейчас действует: <b>${esc(data.effectiveQuota)}</b>. Пусто — значение родителя, <b>0 — без квоты</b>.</p></div>` +

                    '<div class="field"><label for="fsDays">Автоудаление файлов папки, дней</label>' +
                    `<input type="number" id="fsDays" min="0" value="${data.retentionDays ?? ''}" placeholder="не удалять" />` +
                    '<p class="hint">Через сколько дней после загрузки убирать файлы ИЗ ЭТОЙ ПАПКИ. ' +
                    'Убираются они в корзину, а не стираются сразу, — ошибку в сроке можно заметить и исправить. ' +
                    'На вложенные папки срок не распространяется: у каждой свой. ' +
                    '<b>Пусто — не удалять</b>, так и стоит по умолчанию.</p></div>' +

                    '</section>') +

                    '</div>',

                foot: '<button class="btn" type="button" data-mclose>Отмена</button>' +
                    '<button class="btn primary" type="button" id="fsSave">Сохранить</button>'
            });

            $$('[data-mclose]', m).forEach(b => { b.onclick = closeModal; });

            const value = (id, fallback) => {
                const input = $(id, m);

                // Поля пределов есть только у администратора портала.
                // У остальных отправляем то, что было, — иначе сохранение
                // прав молча обнулило бы чужие настройки.
                return input ? input.value : (fallback === null ? '' : String(fallback));
            };

            $('#fsSave', m).onclick = () => {
                submit(save, {
                    'Input.InheritPermissions': $('#fsInherit', m).checked ? 'true' : 'false',
                    'Input.MaxFileSizeMb': value('#fsMax', data.maxFileSizeMb),
                    'Input.QuotaMb': value('#fsQuota', data.quotaMb),
                    'Input.RetentionDays': value('#fsDays', data.retentionDays)
                });
            };

            // Признак «выбран человек, а не группа». Ставится только выбором
            // из дерева: имя, вписанное руками, — это имя группы, так было
            // до появления дерева и так осталось.
            let pickedIsUser = false;

            const field = $('#fsGroup', m);
            const tree = $('#fsTree', m);

            field.addEventListener('input', () => { pickedIsUser = false; });

            $('#fsAdd', m).onclick = () => {
                const group = field.value.trim();

                if (!group) {
                    toast('Выберите группу или сотрудника', 'warn');
                    return;
                }

                post(add, {
                    'NewPermission.GroupName': group,
                    'NewPermission.Access': $('#fsLevel', m).value,
                    'NewPermission.IsUser': pickedIsUser ? 'true' : 'false'
                })
                    .then(r => r.json())
                    .then(answer => {
                        toast(answer.message || 'Доступ выдан', answer.ok ? 'ok' : 'warn');

                        field.value = '';
                        pickedIsUser = false;

                        // Наследование могло выключиться на сервере — оно
                        // выключается само, как только у папки появляются
                        // свои права. Галочку надо привести в соответствие,
                        // иначе «Сохранить» вернёт её обратно.
                        refreshPerms();
                    })
                    .catch(() => toast('Не удалось выдать доступ', 'danger'));
            };

            /** Перечитать права и перерисовать список, не закрывая окно. */
            function refreshPerms() {
                fetch('/Files?handler=FolderSettings&folderId=' + data.id, {
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(fresh => {
                        $('#fsPerms', m).innerHTML = permRows(fresh.permissions);
                        $('#fsInherit', m).checked = !!fresh.inherit;

                        bindDropPerm();
                    })
                    .catch(() => toast('Список прав не обновился — закройте и откройте окно', 'warn'));
            }

            function bindDropPerm() {
                $$('[data-drop-perm]', m).forEach(b => {
                    b.onclick = () => {
                        post('/Files/Settings/' + data.id + '?handler=RemovePermission&returnTo=files'
                            + '&permissionId=' + b.dataset.dropPerm, {})
                            .then(r => r.json())
                            .then(answer => {
                                toast(answer.message || 'Доступ убран', 'ok');
                                refreshPerms();
                            })
                            .catch(() => toast('Не удалось убрать доступ', 'danger'));
                    };
                });
            }

            // ---------- Выбор из дерева домена ----------
            //
            // Раньше имя группы вписывали руками. Это работало, пока имена
            // помнили наизусть; на деле их подсматривают у коллег, ошибаются
            // в раскладке и получают «группа не найдена» без объяснений.
            // Теперь то же самое выбирается из дерева — как в оснастке
            // «Пользователи и компьютеры Active Directory».
            //
            // Уровень запрашивается ОТДЕЛЬНО и только когда его раскрыли:
            // в домене на несколько тысяч учётных записей «дай всё дерево» —
            // это мегабайты и секунды ожидания.

            const path = [];      // раскрытые ветки: [{dn, name}, …]
            let searchTimer = null;

            function icon(kind) {
                return kind === 'ou' ? 'folder' : kind === 'group' ? 'users' : 'home';
            }

            function showTree(dn, query) {
                tree.hidden = false;
                tree.innerHTML = '<div class="dir-load">Читаем каталог…</div>';

                const url = '/Files?handler=Directory&folderId=' + data.id
                    + (dn ? '&dn=' + encodeURIComponent(dn) : '')
                    + (query ? '&q=' + encodeURIComponent(query) : '');

                fetch(url, { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(result => paintTree(result, query))
                    .catch(() => {
                        tree.innerHTML = '<div class="dir-load">Каталог домена сейчас недоступен. '
                            + 'Имя группы можно вписать вручную.</div>';
                    });
            }

            function paintTree(result, query) {
                const crumbs = query
                    ? `<button type="button" class="dir-up" data-up="0">← к дереву</button>`
                    : path.length
                        ? `<button type="button" class="dir-up" data-up="${path.length - 1}">← ${esc(path.length > 1 ? path[path.length - 2].name : 'корень')}</button>`
                        : '<span class="dir-here">Домен</span>';

                const list = (result.nodes || []).map((n, i) =>
                    `<button type="button" class="dir-row" data-i="${i}" data-kind="${n.kind}">` +
                    ic(icon(n.kind), 14) +
                    `<span>${esc(n.name)}</span>` +
                    (n.account ? `<code>${esc(n.account)}</code>` : '<span class="dir-more">›</span>') +
                    '</button>').join('');

                tree.innerHTML = '<div class="dir-head">' + crumbs + '</div>'
                    + (list || '<div class="dir-load">'
                        + esc(result.problem || 'Здесь ничего нет.') + '</div>');

                const up = $('[data-up]', tree);

                if (up) {
                    up.onclick = () => {
                        const to = +up.dataset.up;

                        path.length = query ? 0 : to;
                        field.value = '';
                        showTree(path.length ? path[path.length - 1].dn : '', '');
                    };
                }

                $$('[data-i]', tree).forEach(row => {
                    row.onclick = () => {
                        const node = result.nodes[+row.dataset.i];

                        if (node.kind === 'ou') {
                            path.push({ dn: node.dn, name: node.name });
                            showTree(node.dn, '');

                            return;
                        }

                        // Выбрали группу или человека — подставляем в поле
                        // и закрываем дерево: дальше остаётся нажать «выдать».
                        field.value = node.account;
                        pickedIsUser = node.kind === 'user';
                        tree.hidden = true;
                    };
                });
            }

            field.addEventListener('focus', () => {
                if (tree.hidden) { showTree(path.length ? path[path.length - 1].dn : '', ''); }
            });

            field.addEventListener('input', () => {
                clearTimeout(searchTimer);

                const query = field.value.trim();

                // Ждём, пока человек допечатает: запрос в каталог на каждую
                // букву — это десяток запросов на одно слово.
                searchTimer = setTimeout(() => showTree('', query.length >= 2 ? query : ''), 350);
            });

            // Нажатие мимо дерева его закрывает — как и любое другое меню.
            //
            // Но только НАСТОЯЩЕЕ нажатие мимо. Нажатие по строке дерева
            // тоже доходит сюда — уже после того, как обработчик строки
            // заменил содержимое дерева новым уровнем. Нажатая кнопка
            // к этому времени из страницы удалена, и closest() по ней
            // ничего не находит: получалось, что дерево закрывает само
            // себя при каждом переходе на уровень глубже.
            m.addEventListener('click', e => {
                if (!e.target.isConnected) { return; }

                if (!e.target.closest('.fs-add')) { tree.hidden = true; }
            });

            bindDropPerm();
        }

        function levelName(access) {
            return access === 'Manage' ? 'Управление'
                : access === 'Write' ? 'Запись'
                    : 'Чтение';
        }

        function properties(item) {
            const query = item.file ? 'fileId=' + item.file : 'folderId=' + item.folder;

            fetch('/Files?handler=Properties&' + query, {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                .then(data => openModal({
                    title: esc(data.title),
                    body: '<div class="pv-meta"><h4>Сведения</h4>' +
                        (data.rows || []).map(row =>
                            `<div class="row"><span>${esc(row.name)}</span><b>${esc(row.value)}</b></div>`).join('') +
                        '</div>',
                    foot: '<button class="btn" type="button" data-mclose>Закрыть</button>'
                }))
                .catch(() => toast('Не удалось получить сведения', 'warn'));
        }

        // ---------- Клавиши в списке файлов ----------
        document.addEventListener('keydown', e => {
            if (!selected || /INPUT|TEXTAREA|SELECT/.test(e.target.tagName) || $('#modalRoot .overlay')) {
                return;
            }

            const item = info(selected);

            if (e.key === 'Delete') {
                const many = picked();

                // Выделено несколько — удаляем ВСЁ выделенное. Раньше здесь
                // удалялся только последний нажатый, и это выглядело так,
                // будто клавиша срабатывает через раз.
                if (many.length > 1) {
                    e.preventDefault();
                    removeMany(many.map(info));

                    return;
                }

                if ((item.file && item.canDelete) || (item.folder && item.canManage)) {
                    e.preventDefault();
                    remove(item);
                }
            }

            if (e.key === 'F2' && ((item.file && item.canDelete) || (item.folder && item.canManage))) {
                e.preventDefault();
                rename(item);
            }

            if (e.key === 'Enter') {
                e.preventDefault();
                open(item, selected);
            }
        });

        // ---------- Звёздочка «в избранном» ----------
        container.addEventListener('click', e => {
            const star = e.target.closest('.fav-star');

            if (!star) { return; }

            e.preventDefault();
            e.stopPropagation();

            toggleFav(info(star.closest('[data-id]')));
        });

        // ---------- Кнопка закрепления ----------
        //
        // Рядом со звёздочкой, но это разные вещи: звёздочка личная,
        // закрепление общее — оно поднимает наверх у ВСЕХ, кто заходит
        // в папку. Поэтому кнопка есть только у того, кто папкой управляет.
        container.addEventListener('click', e => {
            const button = e.target.closest('.pin-btn');

            if (!button) { return; }

            e.preventDefault();
            e.stopPropagation();

            togglePin(info(button.closest('[data-id]')));
        });

        // ---------- Кнопки верхней панели ----------
        const newFolderBtn = $('#newFolderBtn');
        const uploadBtn = $('#uploadBtn');
        const fileInput = $('#fileInput');

        if (newFolderBtn) { newFolderBtn.onclick = newFolder; }
        if (uploadBtn && fileInput) { uploadBtn.onclick = () => fileInput.click(); }
        if (fileInput) { fileInput.onchange = e => upload(e.target.files); }

        // Порядок сортировки и вид списка.
        const sortSel = $('#sortSel');

        if (sortSel) { sortSel.onchange = () => sortSel.form.submit(); }

        (function () {
            const gridBtn = $('#gridBtn');
            const listBtn = $('#listBtn');

            if (!gridBtn || !listBtn) { return; }

            const KEY = 'portal.fileView';

            // Вид — дело вкуса конкретного человека, сервер про него
            // не знает: выбор хранится в браузере и действует на всех
            // страницах портала.
            const read = () => {
                try { return localStorage.getItem(KEY); } catch (error) { return null; }
            };

            const remember = mode => {
                try { localStorage.setItem(KEY, mode); } catch (error) { /* пусть будет так */ }
            };

            const apply = mode => {
                const list = mode === 'list';

                $$('.fgrid', container).forEach(x => { x.hidden = list; });
                $$('.flist', container).forEach(x => { x.hidden = !list; });

                gridBtn.classList.toggle('on', !list);
                listBtn.classList.toggle('on', list);
            };

            apply(read() === 'list' ? 'list' : 'grid');

            gridBtn.onclick = () => { apply('grid'); remember('grid'); };
            listBtn.onclick = () => { apply('list'); remember('list'); };
        })();

        // ---------- Загрузка файлов ----------

        /** Размер по-человечески — теми же единицами, что и на сервере. */
        function size(bytes) {
            const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];

            let value = bytes;
            let unit = 0;

            while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }

            return unit === 0
                ? bytes + ' ' + units[0]
                : value.toFixed(value < 10 ? 1 : 0).replace('.0', '').replace('.', ',') + ' ' + units[unit];
        }

        /**
         * Что из выбранного сервер точно не примет.
         *
         * ЗАЧЕМ ПРОВЕРЯТЬ В БРАУЗЕРЕ, ЕСЛИ ЕСТЬ ПРОВЕРКА НА СЕРВЕРЕ
         *
         * Сервер отвергает негодный файл ПОСЛЕ того, как получит его целиком:
         * иначе он не знает ни настоящего размера, ни имени. На канале между
         * офисами это означает, что человек десять минут смотрит на полосу
         * загрузки, чтобы в конце прочитать «файл больше разрешённого».
         * Здесь размер известен сразу, ещё до первого отправленного байта.
         *
         * Проверка в браузере — УДОБСТВО, а не защита: код в браузере можно
         * отключить и обойти. Настоящая проверка остаётся на сервере,
         * в UploadValidator, и правила здесь повторяют её один в один.
         */
        function rejected(list) {
            const maxFile = parseInt(settings.maxFileSize, 10) || 0;
            const quota = settings.quota === '' ? null : parseInt(settings.quota, 10);
            const blocked = (settings.blocked || '').split(' ').filter(Boolean);

            let used = parseInt(settings.used, 10) || 0;

            const bad = [];

            [...list].forEach(file => {
                const dot = file.name.lastIndexOf('.');
                const extension = dot > 0 ? file.name.slice(dot).toLowerCase() : '';

                if (file.size === 0) {
                    bad.push(`«${file.name}» — файл пустой`);
                    return;
                }

                if (extension && blocked.indexOf(extension) >= 0) {
                    bad.push(`«${file.name}» — файлы ${extension} загружать запрещено`);
                    return;
                }

                if (maxFile > 0 && file.size > maxFile) {
                    bad.push(`«${file.name}» — ${size(file.size)}, ` +
                        `а для этой папки разрешено не больше ${size(maxFile)}`);
                    return;
                }

                if (quota !== null && used + file.size > quota) {
                    bad.push(`«${file.name}» — в папке не хватает места: ` +
                        `свободно ${size(Math.max(0, quota - used))}, нужно ${size(file.size)}`);
                    return;
                }

                // Место, занятое годными файлами, учитываем сразу: иначе
                // десять файлов по гигабайту поодиночке «влезают» в квоту,
                // а вместе — нет, и об этом сообщил бы уже сервер.
                used += file.size;
            });

            return bad;
        }

        function upload(list) {
            if (!list || !list.length || !canWrite) { return; }

            const bad = rejected(list);

            if (bad.length) {
                // Годные файлы всё равно не отправляем: человек выбрал папку
                // целиком и ждёт, что уедет она целиком. Отправить половину
                // молча — худший из вариантов, потому что заметят это нескоро.
                alertDlg(
                    bad.length === 1 ? 'Этот файл загрузить нельзя' : `Не пройдут проверку: ${bad.length}`,
                    bad.slice(0, 10).join('\n')
                        + (bad.length > 10 ? `\n…и ещё ${bad.length - 10}` : '')
                        + '\n\nНичего не загружено. Уберите эти файлы из выбранного и повторите.');

                return;
            }

            const data = new FormData();

            data.append('__RequestVerificationToken', token());

            [...list].forEach(f => data.append('uploads', f, f.name));

            const box = el('<div class="uploader"><b>Загрузка: ' + list.length + ' файл(ов)…</b>' +
                '<div class="bar"><i></i></div><p>0%</p></div>');

            document.body.appendChild(box);

            const bar = $('i', box);
            const percent = $('p', box);

            // XMLHttpRequest, а не fetch: только он умеет сообщать, сколько
            // уже отправлено. На канале между офисами файл может идти
            // долго, и без полосы человек решит, что портал завис.
            const request = new XMLHttpRequest();

            request.open('POST', '/Files?handler=Upload&folderId=' + folderId);
            request.setRequestHeader('X-Requested-With', 'XMLHttpRequest');

            request.upload.onprogress = e => {
                if (!e.lengthComputable) { return; }

                const value = Math.round(e.loaded / e.total * 100);

                bar.style.width = value + '%';
                percent.textContent = value + '%';
            };

            request.onload = () => {
                box.remove();

                // Сервер отвечает перенаправлением на ту же папку —
                // перезагружаем страницу, чтобы увидеть загруженное
                // вместе со всеми проверками, которые сервер уже сделал.
                window.location.reload();
            };

            request.onerror = () => {
                box.remove();
                toast('Не удалось загрузить файлы: связь прервалась', 'danger');
            };

            request.send(data);
        }

        // Перетаскивание файлов из проводника.
        if (canWrite) {
            let depth = 0;

            /**
             * Тащат ли файлы ИЗ СИСТЕМЫ, а не плитку по самой странице.
             *
             * Различать обязательно: поле «отпустите файлы для загрузки»
             * растянуто на всю область папки и лежит поверх плиток. Появляясь
             * при перетаскивании плитки, оно перехватывало отпускание на себя,
             * и перемещение внутри портала не срабатывало вовсе — файл будто
             * возвращался на место.
             *
             * Признак — типы в обмене: у файлов из проводника там Files,
             * у нашей плитки только текст.
             */
            const fromOutside = e =>
                !!e.dataTransfer && [...e.dataTransfer.types].includes('Files');

            files.addEventListener('dragenter', e => {
                if (!fromOutside(e)) { return; }

                e.preventDefault();
                depth++;
                files.classList.add('dragging');
            });

            files.addEventListener('dragover', e => {
                if (fromOutside(e)) { e.preventDefault(); }
            });

            files.addEventListener('dragleave', e => {
                if (!fromOutside(e)) { return; }

                depth = Math.max(0, depth - 1);

                if (depth === 0) { files.classList.remove('dragging'); }
            });

            files.addEventListener('drop', e => {
                if (!fromOutside(e)) { return; }

                e.preventDefault();
                depth = 0;
                files.classList.remove('dragging');

                if (e.dataTransfer && e.dataTransfer.files.length) { upload(e.dataTransfer.files); }
            });

            // Ctrl+V на странице папки: вставка файла из буфера обмена Windows.
            document.addEventListener('paste', e => {
                if (/INPUT|TEXTAREA/.test(e.target.tagName)) { return; }

                const items = e.clipboardData && e.clipboardData.files;

                if (items && items.length) { upload(items); }
            });
        }
    }

    // ======================================================================
    // 8. Предпросмотр
    //
    // Окно поверх страницы: слева содержимое файла, справа колонка сведений.
    // Что именно показать, решает сервер — он же отдаёт только то, что есть
    // в белом списке (см. PreviewSupport.cs).
    // ======================================================================


    // ======================================================================
    // 9. Переписки
    //
    // Страница обычная, серверная: список бесед и сообщения приходят готовыми.
    // Здесь только то, ради чего нужен код на странице:
    //   * отправка по Enter и растущее поле ввода;
    //   * выбор собеседников (окно с поиском по справочнику);
    //   * управление группой;
    //   * проверка, не написал ли кто-нибудь, пока страница открыта.
    // ======================================================================

    const chat = $('[data-chat]');

    if (chat) {
        const settings = chat.dataset;
        const conversationId = settings.conversation || '';
        const me = settings.me || '';

        // Администратор портала, открывший ЧУЖУЮ переписку. Ему доступно
        // удаление сообщений, но не правка: чужие слова менять нельзя.
        const isAdminObserver = settings.observer === 'true';

        // ---------- Лента сообщений ----------
        const msgs = $('#msgs');

        // Прокручиваем к последнему: человек открывает переписку, чтобы
        // увидеть свежее, а не то, что писали полгода назад.
        if (msgs) { msgs.scrollTop = msgs.scrollHeight; }

        // ---------- Поле ввода ----------
        const form = $('#chatForm');
        const text = $('#chatText');
        const files = $('#chatFiles');
        const attached = $('#chatAttached');

        if (text) {
            // Поле растёт вместе с текстом, но не выше предела из стилей.
            const grow = () => {
                text.style.height = 'auto';

                const height = Math.min(text.scrollHeight, 110);

                text.style.height = height + 'px';

                // Пока поле в одну строку, кнопки стоят вровень с ним
                // по середине; как только текст занял несколько строк —
                // прижимаются к нижней. Признак ставится классом, потому
                // что выравнивание задаётся стилями всей строки.
                if (form) { form.classList.toggle('tall', height > 46); }
            };

            text.addEventListener('input', grow);
            grow();

            text.addEventListener('keydown', e => {
                // Enter отправляет, Shift+Enter переводит строку — так же,
                // как в любом мессенджере. Иначе придётся тянуться к мыши
                // после каждой реплики.
                if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();

                    if (text.value.trim() || (files && files.files.length)) {
                        form.requestSubmit();
                    }
                }
            });
        }

        const attachBtn = $('#attachBtn');

        if (attachBtn && files) {
            // Скрепка предлагает выбор: файл с компьютера или файл, который
            // уже лежит в портале.
            //
            // Второе — не «ещё одна копия того же документа», а ССЫЛКА
            // на него. Разница существенная: договор на сотню мегабайт
            // не нужно ни отправлять, ни хранить второй раз, а получатель
            // увидит ту версию, которая лежит в папке сейчас, — не ту,
            // которая лежала в день отправки. Права при переходе по ссылке
            // проверяются как обычно: у кого доступа к папке нет,
            // тот файл не откроет.
            attachBtn.onclick = e => {
                const rect = attachBtn.getBoundingClientRect();

                showMenu(rect.left, rect.bottom + 4, [
                    { icon: 'upload', label: 'Файл с компьютера', fn: () => files.click() },
                    { icon: 'folder', label: 'Ссылка на файл портала', fn: pickPortalFile }
                ]);

                e.stopPropagation();
            };

            files.onchange = () => {
                // Слишком крупное вложение отсеиваем сразу. Сервер узнаёт
                // настоящий размер, только получив файл целиком, — то есть
                // сообщил бы об отказе после всего ожидания.
                const limit = parseInt(settings.maxAttachment, 10) || 0;

                const tooBig = limit > 0
                    ? [...files.files].filter(f => f.size > limit)
                    : [];

                if (tooBig.length) {
                    alertDlg(
                        'Вложение слишком большое',
                        tooBig.map(f => `«${f.name}» — ${fmtSize(f.size)}`).join('\n')
                            + `\n\nВ переписку можно приложить файл не больше ${fmtSize(limit)}.`
                            + '\nКрупный файл загрузите в «Файлы» и пришлите ссылку на него.');

                    // Сбрасываем ВЕСЬ выбор, а не только крупные файлы:
                    // выборочно вычистить FileList нельзя, а отправить часть
                    // молча — худший вариант, такое замечают нескоро.
                    files.value = '';
                }

                if (!attached) { return; }

                attached.hidden = files.files.length === 0;

                attached.innerHTML = [...files.files]
                    .map(f => `<span class="chip">${esc(f.name)} · ${fmtSize(f.size)}</span>`)
                    .join('');
            };
        }

        /**
         * Выбор файла из хранилища портала.
         *
         * Ходим по дереву тем же обработчиком, что и страница «Файлы»
         * (handler=Browse), поэтому окно показывает ровно то, что человеку
         * и так видно, — ни папкой больше.
         *
         * В сообщение попадает полный адрес файла. Он же превращается
         * в кликабельную ссылку разбором текста (linkify) — тем самым,
         * который делает ссылками любые адреса в переписке. Отдельного
         * вида вложения ради этого заводить не пришлось.
         */
        function pickPortalFile() {
            const m = openModal({
                wide: true,
                title: 'Файл из портала',
                body: '<p class="hint" id="fpPath">Файлы</p>' +
                    '<div class="fp-list" id="fpList"><p class="hint">Читаем…</p></div>' +
                    '<p class="hint">В сообщение уйдёт ссылка, а не копия файла: ' +
                    'получатель откроет его, если у него есть доступ к папке.</p>'
            });

            const list = $('#fpList', m);
            const path = $('#fpPath', m);

            function load(id) {
                list.innerHTML = '<p class="hint">Читаем…</p>';

                fetch('/Files?handler=Browse' + (id ? '&folderId=' + id : ''), {
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(data => {
                        path.textContent = data.path;

                        const rows = [];

                        if (!data.atRoot) {
                            rows.push('<button class="fp-row fp-up" type="button" data-up="' +
                                (data.parentId === null || data.parentId === undefined ? '' : data.parentId) +
                                `">${ic('back', 16)}<span>Наверх</span></button>`);
                        }

                        (data.folders || []).forEach(f => {
                            rows.push(`<button class="fp-row" type="button" data-open="${f.id}">` +
                                `${ic('folder', 16)}<span>${esc(f.name)}</span></button>`);
                        });

                        (data.files || []).forEach(f => {
                            rows.push('<button class="fp-row fp-file" type="button"' +
                                ` data-pick="${esc(f.href)}" data-title="${esc(f.name)}">` +
                                `${ic('file', 16)}<span>${esc(f.name)}</span>` +
                                `<em>${esc(f.size)}</em></button>`);
                        });

                        list.innerHTML = rows.length
                            ? rows.join('')
                            : '<p class="hint">Здесь пусто.</p>';

                        $$('[data-open]', list).forEach(b => {
                            b.onclick = () => load(b.dataset.open);
                        });

                        $$('[data-up]', list).forEach(b => {
                            b.onclick = () => load(b.dataset.up);
                        });

                        $$('[data-pick]', list).forEach(b => {
                            b.onclick = () => {
                                const url = location.origin + b.dataset.pick;

                                // Дописываем к тому, что уже набрано, а не
                                // заменяем: ссылку обычно шлют с пояснением.
                                text.value = (text.value ? text.value.replace(/\s*$/, '\n') : '') + url + '\n';
                                text.dispatchEvent(new Event('input'));

                                closeModal();
                                text.focus();

                                toast('Ссылка на «' + b.dataset.title + '» добавлена', 'info');
                            };
                        });
                    })
                    .catch(() => {
                        list.innerHTML = '<p class="hint">Не удалось прочитать список файлов.</p>';
                    });
            }

            load('');
        }

        // ---------- Удаление и правка сообщения ----------

        function deleteMessage(id) {
            confirmDlg('Удалить сообщение?',
                'Сообщение исчезнет у всех участников переписки. Отменить это нельзя.',
                'Удалить',
                () => submit('/Messages?handler=DeleteMessage&id=' + conversationId,
                    { messageId: id }),
                true);
        }

        function editMessage(node) {
            promptDlg('Изменить сообщение', 'Текст', node.dataset.body || '', 'Сохранить', value => {
                submit('/Messages?handler=EditMessage&id=' + conversationId,
                    { messageId: node.dataset.message, newBody: value });
            });
        }

        chat.addEventListener('click', e => {
            const button = e.target.closest('[data-delete-message]');

            if (button) { deleteMessage(button.dataset.deleteMessage); }
        });

        // Контекстное меню на сообщении: правка и удаление.
        //
        // Кнопка-корзинка рядом со временем осталась — она привычна и видна
        // сразу. Но правка третьей кнопкой в пузырьке уже не помещается,
        // а меню по правой кнопке в портале работает везде, где есть список,
        // и в переписке его отсутствие было заметным исключением.
        chat.addEventListener('contextmenu', e => {
            const node = e.target.closest('[data-message]');

            if (!node) { return; }

            // Выделенный текст оставляем браузеру: его меню там и нужно —
            // «копировать», «искать в интернете».
            const selection = window.getSelection();

            if (selection && !selection.isCollapsed) { return; }

            e.preventDefault();

            const mine = node.dataset.mine === 'true';
            const deleted = node.dataset.deleted === 'true';
            const body = node.dataset.body || '';
            const entries = [];

            if (body) {
                entries.push({ icon: 'copy', label: 'Копировать текст', fn: () => copyText(body, 'Текст скопирован') });
            }

            // Править может только автор — и только пока сообщение не удалено.
            // Чужие слова не должен менять никто, включая администратора:
            // удаление оставляет честную пометку, а правка подменяет сказанное.
            if (mine && !deleted) {
                entries.push({ icon: 'edit', label: 'Изменить', fn: () => editMessage(node) });
            }

            if (!deleted && (mine || isAdminObserver)) {
                entries.push({ sep: 1 });
                entries.push({
                    icon: 'trash', label: 'Удалить', danger: 1,
                    fn: () => deleteMessage(node.dataset.message)
                });
            }

            if (entries.length === 0) { return; }

            showMenu(e.clientX, e.clientY, entries);
        });

        // ---------- Контекстное меню на переписке в списке ----------
        chat.addEventListener('contextmenu', e => {
            const node = e.target.closest('[data-convo]');

            if (!node) { return; }

            e.preventDefault();

            const id = node.dataset.convo;
            const title = node.dataset.convoTitle || 'переписка';
            const group = node.dataset.convoGroup === 'true';

            const entries = [
                { icon: 'chat', label: 'Открыть', fn: () => { window.location.href = '/Messages?id=' + id; } },
                { icon: 'share', label: 'Скопировать ссылку', fn: () => copyText(location.origin + '/Messages?id=' + id, 'Ссылка на переписку скопирована') }
            ];

            entries.push({ sep: 1 });

            // Группу удаляет только тот, кто её создал; остальные из неё
            // выходят. Переписка десяти человек не должна исчезать оттого,
            // что одному из них она надоела. Права проверяет сервер заново —
            // здесь только вид меню.
            if (group) {
                entries.push({
                    icon: 'logout', label: 'Выйти из группы',
                    fn: () => confirmDlg('Выйти из группы?',
                        `Группа «${title}» пропадёт из вашего списка. Остальные участники останутся.`,
                        'Выйти',
                        () => submit('/Messages?handler=RemoveMember&id=' + id, { memberUserName: me }),
                        true)
                });
            }

            entries.push({
                icon: 'trash', label: group ? 'Удалить группу' : 'Удалить переписку', danger: 1,
                fn: () => confirmDlg(
                    group ? 'Удалить группу?' : 'Удалить переписку?',
                    group
                        ? `Группа «${title}» будет удалена вместе со всеми сообщениями и вложениями — у всех участников. Отменить это нельзя.`
                        : `Переписка «${title}» будет удалена вместе со всеми сообщениями и вложениями. Она исчезнет и у собеседника — отменить это нельзя.`,
                    'Удалить',
                    () => submit('/Messages?handler=DeleteConversation&id=' + id),
                    true)
            });

            showMenu(e.clientX, e.clientY, entries);
        });

        // ---------- Выбор людей ----------
        //
        // Один и тот же список сотрудников нужен в трёх местах: «написать»,
        // «создать группу», «добавить в группу». Поэтому он собран одной
        // функцией, а различается только тем, что делать с выбранным.

        /**
         * Окно со строкой поиска и списком сотрудников.
         *
         * multi = false — нажатие сразу выполняет действие и закрывает окно;
         * multi = true  — выбранные накапливаются, действие по кнопке внизу.
         */
        function peoplePicker(options) {
            const chosen = new Map();

            const m = openModal({
                title: esc(options.title),
                body: (options.extra || '') +
                    '<div class="field"><label for="peopleSearch">' + esc(options.label) + '</label>' +
                    '<input type="text" id="peopleSearch" autocomplete="off" ' +
                    'placeholder="Начните вводить фамилию или логин"></div>' +
                    '<div class="chosen" id="peopleChosen" hidden></div>' +
                    '<div class="people" id="peopleList"><p class="hint">Загружается…</p></div>',
                foot: options.multi
                    ? '<button class="btn" type="button" data-mclose>Отмена</button>' +
                      '<button class="btn primary" type="button" id="peopleOk">' + esc(options.okLabel) + '</button>'
                    : '<button class="btn" type="button" data-mclose>Отмена</button>'
            });

            const search = $('#peopleSearch', m);
            const list = $('#peopleList', m);
            const chosenBox = $('#peopleChosen', m);

            const paintChosen = () => {
                chosenBox.hidden = chosen.size === 0;

                chosenBox.innerHTML = [...chosen.values()]
                    .map(p => `<span class="chip">${esc(p.displayName)}</span>`).join('');
            };

            const load = () => {
                fetch('/Messages?handler=People&q=' + encodeURIComponent(search.value.trim()), {
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(people => {
                        if (people.length === 0) {
                            list.innerHTML = '<p class="hint">Никого не нашлось. ' +
                                'Справочник сотрудников читается из Active Directory.</p>';
                            return;
                        }

                        list.innerHTML = people.map(p =>
                            `<button class="member-row" type="button" data-login="${esc(p.userName)}" ` +
                            `data-name="${esc(p.displayName)}">` +
                            `<div class="avatar" data-av="${avatarIndex(p.displayName)}">${esc(initials(p.displayName))}</div>` +
                            `<div><b>${esc(p.displayName)}</b><span>${esc(p.userName)}</span></div>` +
                            (options.multi ? '<span class="member-mark"></span>' : '') +
                            '</button>').join('');

                        $$('.member-row', list).forEach(row => {
                            const login = row.dataset.login;

                            if (chosen.has(login)) { row.classList.add('on'); }

                            row.onclick = () => {
                                if (!options.multi) {
                                    closeModal();
                                    options.onPick(login, row.dataset.name);
                                    return;
                                }

                                if (chosen.has(login)) {
                                    chosen.delete(login);
                                    row.classList.remove('on');
                                } else {
                                    chosen.set(login, { userName: login, displayName: row.dataset.name });
                                    row.classList.add('on');
                                }

                                paintChosen();
                            };
                        });
                    })
                    .catch(() => {
                        list.innerHTML = '<p class="hint">Не удалось получить список сотрудников. ' +
                            'Возможно, недоступен контроллер домена.</p>';
                    });
            };

            // Ждём, пока человек допечатает: запрос уходит в Active Directory,
            // и дёргать его на каждую букву незачем.
            let timer = null;

            search.addEventListener('input', () => {
                clearTimeout(timer);
                timer = setTimeout(load, 250);
            });

            setTimeout(() => search.focus(), 60);
            load();

            if (options.multi) {
                $('#peopleOk', m).onclick = () => {
                    if (chosen.size === 0) {
                        toast('Никто не выбран', 'warn');
                        return;
                    }

                    closeModal();
                    options.onDone([...chosen.keys()], m);
                };
            }

            return m;
        }

        /** Те же буквы и цвет кружка, что считает сервер (см. Avatars). */
        function initials(name) {
            const parts = String(name || '').split(/[\s._-]+/).filter(Boolean);

            if (parts.length === 0) { return '?'; }

            return (parts.length === 1 ? parts[0][0] : parts[0][0] + parts[1][0]).toUpperCase();
        }

        function avatarIndex(name) {
            let sum = 0;

            for (const c of String(name || '')) {
                sum = (sum * 31 + c.charCodeAt(0)) & 0x7fffffff;
            }

            return sum % 8;
        }

        // ---------- Кнопки ----------
        const newDirect = $('#newDirectBtn');

        if (newDirect) {
            newDirect.onclick = () => peoplePicker({
                title: 'Написать сотруднику',
                label: 'Кому',
                onPick: login => submit('/Messages?handler=Start', { withUserName: login })
            });
        }

        const newGroup = $('#newGroupBtn');

        if (newGroup) {
            newGroup.onclick = () => {
                const m = peoplePicker({
                    title: 'Создать группу',
                    label: 'Кого добавить',
                    multi: true,
                    okLabel: 'Создать',
                    extra: '<div class="field"><label for="groupTitle">Название группы</label>' +
                        '<input type="text" id="groupTitle" maxlength="120" placeholder="Например: Отдел кадров"></div>',
                    onDone: logins => {
                        const title = ($('#groupTitle', m) || {}).value || '';

                        submit('/Messages?handler=CreateGroup', {
                            GroupTitle: title.trim() || 'Новая группа',
                            Members: logins
                        });
                    }
                });
            };
        }

        const groupBtn = $('#groupBtn');

        if (groupBtn) {
            groupBtn.onclick = () => openGroupWindow();
        }

        /** Окно «Участники группы»: список, переименование, выход. */
        function openGroupWindow() {
            const isOwner = settings.isOwner === 'true';

            // Список участников уже есть на странице — берём его оттуда,
            // а не ходим на сервер второй раз за тем же самым.
            const rows = $$('[data-participant]').map(node => ({
                login: node.dataset.participant,
                name: node.dataset.name,
                owner: node.dataset.owner === 'true'
            }));

            const m = openModal({
                title: 'Участники группы',
                body: (isOwner
                    ? '<div class="field"><label for="groupNewTitle">Название группы</label>' +
                      '<div class="inline-pair"><input type="text" id="groupNewTitle" maxlength="120" ' +
                      `value="${esc(settings.title || '')}">` +
                      '<button class="btn" type="button" id="groupRename">Сохранить</button></div></div>'
                    : '') +
                    '<div class="people">' + rows.map(p =>
                        '<div class="member-row member-row--static">' +
                        `<div class="avatar" data-av="${avatarIndex(p.name)}">${esc(initials(p.name))}</div>` +
                        `<div><b>${esc(p.name)}</b><span>${p.owner ? 'создатель' : 'участник'}</span></div>` +
                        (isOwner && p.login !== settings.me
                            ? `<button class="link danger" type="button" data-drop="${esc(p.login)}">убрать</button>`
                            : '') +
                        '</div>').join('') + '</div>',
                foot: (isOwner ? '<button class="btn" type="button" id="groupAdd">Добавить человека</button>' : '') +
                    '<button class="btn danger" type="button" id="groupLeave">Выйти из группы</button>' +
                    '<button class="btn" type="button" data-mclose>Закрыть</button>'
            });

            const rename = $('#groupRename', m);

            if (rename) {
                rename.onclick = () => submit('/Messages?handler=RenameGroup&id=' + conversationId,
                    { newTitle: $('#groupNewTitle', m).value });
            }

            $$('[data-drop]', m).forEach(b => {
                b.onclick = () => submit('/Messages?handler=RemoveMember&id=' + conversationId,
                    { memberUserName: b.dataset.drop });
            });

            const add = $('#groupAdd', m);

            if (add) {
                add.onclick = () => peoplePicker({
                    title: 'Добавить в группу',
                    label: 'Кого добавить',
                    onPick: login => submit('/Messages?handler=AddMember&id=' + conversationId,
                        { memberUserName: login })
                });
            }

            $('#groupLeave', m).onclick = () => {
                closeModal();

                confirmDlg('Выйти из группы?',
                    'Вы перестанете получать сообщения этой группы. Вернуться можно, только если вас добавят заново.',
                    'Выйти',
                    () => submit('/Messages?handler=RemoveMember&id=' + conversationId,
                        { memberUserName: settings.me }),
                    true);
            };
        }

        // ---------- Живая переписка ----------
        //
        // Новые сообщения ДОРИСОВЫВАЮТСЯ прямо в ленту, а не предлагаются
        // кнопкой «обновить»: раньше человеку приходилось перезагружать
        // страницу, чтобы увидеть ответ, — то есть переписка работала
        // как почта.
        //
        // Страницу целиком при этом не перезагружаем: в поле ввода может
        // быть набранный ответ, и обновление стёрло бы его.
        //
        // Опрос, а не постоянное соединение: между офисами канал узкий
        // и рвётся, а висящее соединение там регулярно обрывается —
        // см. те же соображения в PresenceService.
        if (conversationId) {
            const CHECK_MS = 5000;

            let lastId = parseInt(settings.lastMessage, 10) || 0;
            let busy = false;

            function bubble(msg) {
                const box = el('<div class="msg' + (msg.mine ? ' mine' : '') + '"></div>');

                box.dataset.message = msg.id;
                box.dataset.mine = String(msg.mine);
                box.dataset.deleted = String(msg.deleted);
                box.dataset.body = msg.body || '';

                if (!msg.mine) {
                    box.appendChild(el(`<div class="avatar">${esc(initials(msg.author))}</div>`));
                }

                const files = (msg.files || []).map(f =>
                    `<a class="b-file" href="/Messages?handler=Attachment&fileId=${f.id}">` +
                    `<span class="b-fname">${esc(f.name)}</span>` +
                    `<span class="b-fsize">${esc(f.size)}</span></a>`).join('');

                const name = (!msg.mine && settings.isGroup === 'true') || isAdminObserver
                    ? `<div class="b-name">${esc(msg.author)}` +
                      (msg.ip ? `<span class="b-ip">${esc(msg.ip)}</span>` : '') + '</div>'
                    : '';

                const text = msg.deleted
                    ? '<div class="b-text b-deleted">сообщение удалено</div>'
                    : (msg.body ? `<div class="b-text">${linkify(msg.body)}</div>` : '');

                box.appendChild(el(
                    '<div class="bubble">' + name + text + files +
                    '<div class="b-time">' +
                    (msg.edited ? '<span class="b-edited">изменено</span>' : '') +
                    esc(msg.at) +
                    (msg.mine && !msg.deleted && !isAdminObserver
                        ? `<span class="b-ticks" title="Отправлено">${ic('check', 15)}</span>`
                        : '') +
                    '</div></div>'));

                return box;
            }

            /** Инициалы для кружка — тем же правилом, что и на сервере. */
            function initials(name) {
                const parts = (name || '').trim().split(/\s+/).filter(Boolean);

                return parts.length === 0 ? '?'
                    : (parts[0][0] + (parts[1] ? parts[1][0] : '')).toUpperCase();
            }

            /** Адреса в тексте — ссылками. Всё остальное экранируется. */
            function linkify(body) {
                return esc(body)
                    .replace(/\n/g, '<br />')
                    .replace(/(https?:\/\/[^\s<]+)/g,
                        '<a href="$1" rel="noopener noreferrer" target="_blank">$1</a>');
            }

            setInterval(() => {
                if (document.visibilityState !== 'visible' || busy) { return; }

                busy = true;

                fetch(`/Messages?handler=New&id=${conversationId}&afterId=${lastId}`, {
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
                    .then(data => {
                        // Галочки уже показанных сообщений: собеседник мог
                        // дочитать до какого-то места, пока мы смотрели.
                        $$('.msg.mine .b-ticks', chat).forEach(tick => {
                            const id = parseInt(tick.closest('[data-message]').dataset.message, 10);
                            const read = id <= (data.readUpTo || 0);

                            tick.classList.toggle('read', read);
                            tick.title = read ? 'Прочитано' : 'Отправлено';
                            $('use', tick).setAttribute('href', read ? '#i-check-all' : '#i-check');
                        });

                        if (!data.hasNew) { return; }

                        // Дорисовываем в конец и прокручиваем — но только
                        // если человек и так смотрел на низ ленты. Если он
                        // ушёл читать вверх, дёргать его прокруткой нельзя.
                        const atBottom = msgs
                            && msgs.scrollHeight - msgs.scrollTop - msgs.clientHeight < 80;

                        (data.messages || []).forEach(msg => {
                            if ($(`[data-message="${msg.id}"]`, chat)) { return; }

                            msgs.appendChild(bubble(msg));
                            lastId = Math.max(lastId, msg.id);

                            if (!msg.mine) {
                                toast('Новое сообщение: ' + msg.author, 'info');
                            }
                        });

                        if (atBottom) { msgs.scrollTop = msgs.scrollHeight; }
                    })
                    .catch(() => { /* связь могла моргнуть — молчим */ })
                    .finally(() => { busy = false; });
            }, CHECK_MS);
        }
    }

    /** Сколько ждём ответа, прежде чем признать, что связи нет. */
    const WAIT_MS = 45000;

    /**
     * Окно предпросмотра.
     *
     * Открывает не только файлы хранилища: тем же окном показываются
     * вложения объявлений. Поэтому адреса берутся из самого item
     * (src — сам файл, doc — разобранная разметка для Office и архивов),
     * а не собираются здесь из номера файла. Без них подставляются
     * обработчики «Файлов» — обычный случай.
     *
     * Правая колонка со сведениями и правами есть только у файлов
     * хранилища: у вложения объявления нет ни папки, ни прав на неё,
     * и показывать там нечего.
     */
    function openPreview(item, node) {
        const source = item.src || ('/Files?handler=Preview&fileId=' + item.file);
        const stored = !!item.file;

        // Значок берём прямо из плитки: он уже нарисован сервером,
        // и рисовать его второй раз здесь — лишнее удвоение кода,
        // которое неминуемо разъедется с серверным.
        const icon = node ? node.querySelector('.tile-icon svg, .ficon svg') : null;

        const m = openModal({
            wide: true,
            title: `<span class="pv-title">${icon ? icon.outerHTML : ''}<span>${esc(item.name)}</span></span>`,
            body: '<div class="pv-body' + (stored ? '' : ' pv-body--bare') + '">' +
                // Полоса масштаба лежит НЕ внутри прокручиваемой области,
                // а в неподвижной обёртке вокруг неё: внутри она ездила
                // вместе с документом и при прокрутке вниз уходила вверх
                // за край окна.
                '<div class="pv-stage">' +
                '<div class="pv-viewer" id="pvViewer">' +
                '<div class="pv-load"><div class="pv-spin"></div><p>Загружается…</p></div>' +
                '</div>' +
                '</div>' +
                (stored
                    ? '<div class="pv-aside">' +
                      '<div class="pv-actions">' +
                      `<button class="btn sm" type="button" id="pvFav">${ic('star', 14)}Избранное</button>` +
                      `<button class="btn sm" type="button" id="pvShare">${ic('share', 14)}Ссылка</button>` +
                      `<a class="btn sm" id="pvDl" href="${esc(item.href)}" download>${ic('download', 14)}</a>` +
                      '</div>' +
                      '<div class="pv-meta" id="pvFacts"><h4>Сведения</h4></div>' +
                      '<div class="pv-meta" id="pvAccess"><h4>Доступ</h4></div>' +
                      '</div>'
                    : '<div class="pv-aside pv-aside--slim">' +
                      '<div class="pv-actions">' +
                      `<a class="btn sm" href="${esc(item.href)}" download>${ic('download', 14)}Скачать</a>` +
                      '</div></div>') +
                '</div>'
        });

        const viewer = $('#pvViewer', m);

        loadViewer(item, source, viewer);

        if (!stored) { return; }

        loadFacts(item, m);

        $('#pvFav', m).onclick = () => {
            post('/Files?handler=Favorite&fileId=' + item.file, {})
                .then(r => r.json())
                .then(d => toast(d.favorite ? 'Добавлено в избранное' : 'Убрано из избранного', 'info'))
                .catch(() => toast('Не удалось изменить избранное', 'danger'));
        };

        $('#pvShare', m).onclick = () => {
            // Ссылка именно НА ФАЙЛ, а не на страницу папки: по ней файл
            // сразу откроется или скачается. Ссылка обычная, не публичная —
            // анонимного доступа портал не даёт, и откроет её только тот,
            // у кого и так есть доступ к папке.
            copyText(location.origin + item.href, 'Ссылка на файл скопирована');
        };
    }

    /** Колонки «Сведения» и «Доступ» — те же данные, что и в окне свойств. */
    function loadFacts(item, m) {
        fetch('/Files?handler=Properties&fileId=' + item.file, {
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
            .then(data => {
                $('#pvFacts', m).innerHTML = '<h4>Сведения</h4>' +
                    (data.rows || []).map(row => {
                        // Длинное значение без пробелов (например, тип содержимого
                        // application/vnd.openxmlformats-officedocument…) в узкую
                        // колонку рядом с подписью не помещается и рвётся посреди
                        // слова. Такие значения ставим под подписью, во всю ширину.
                        const value = String(row.value === null || row.value === undefined ? '' : row.value);
                        const wide = value.length > 28 && !value.includes(' ');

                        return `<div class="row${wide ? ' row--wide' : ''}">` +
                            `<span>${esc(row.name)}</span><b>${esc(value)}</b></div>`;
                    }).join('');

                const groups = data.access || [];

                $('#pvAccess', m).innerHTML = '<h4>Доступ</h4>' +
                    (groups.length
                        ? groups.map(g => `<div class="acc-row">${esc(g)}</div>`).join('')
                        : '<div class="acc-row">Действуют права родительской папки</div>');
            })
            .catch(() => {
                // Сведения — дополнение, а не главное. Не получилось —
                // окно всё равно показывает файл, ругаться незачем.
            });
    }

    function loadViewer(item, source, viewer) {
        const show = html => { viewer.innerHTML = html; };

        /**
         * Полоса масштаба. Кладётся в обёртку .pv-stage, а не в саму область
         * просмотра: та прокручивается вместе с содержимым, и полоса
         * уезжала бы вверх вместе с первой страницей документа.
         */
        const zoomBar = html => {
            const stage = viewer.parentElement;
            const previous = $('.zoom-bar', stage);

            if (previous) { previous.remove(); }

            stage.insertAdjacentHTML('beforeend', html);

            return stage;
        };

        const failed = (text, detail) => {
            viewer.innerHTML = `<div class="empty">${ic('alert', 60, 1)}<b>${esc(text)}</b>` +
                `<p>${esc(detail || '')}</p>` +
                `<a class="btn" href="${esc(item.href)}" download>${ic('download', 14)}Скачать файл</a></div>`;
        };

        const html = (url, wrap, done) => {
            const controller = typeof AbortController === 'function' ? new AbortController() : null;
            let timedOut = false;

            const timer = setTimeout(() => {
                timedOut = true;
                if (controller) { controller.abort(); }
            }, WAIT_MS);

            const options = { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } };

            if (controller) { options.signal = controller.signal; }

            fetch(url, options)
                .then(r => { clearTimeout(timer); return r.ok ? r.text() : Promise.reject(r.status); })
                .then(markup => { show(wrap ? wrap(markup) : markup); if (done) { done(); } })
                .catch(reason => {
                    clearTimeout(timer);

                    failed('Не удалось получить содержимое',
                        timedOut ? 'ответ не пришёл за ' + Math.round(WAIT_MS / 1000) + ' с'
                            : reason === 401 ? 'вход в портал истёк — обновите страницу'
                                : 'сервер ответил кодом ' + reason);
                });
        };

        if (item.kind === 'image') {
            const image = new Image();

            image.onload = () => {
                show('<div class="pv-img"><img id="pvImg" alt=""></div>');

                const stage = zoomBar('<div class="zoom-bar">' +
                    `<button class="icon-btn" type="button" data-z="-1" title="Отдалить">${ic('zoomout', 16)}</button>` +
                    '<button class="icon-btn zoom-val" type="button" data-z="0" id="zLbl" title="Вернуть 100%">100%</button>' +
                    `<button class="icon-btn" type="button" data-z="1" title="Приблизить">${ic('zoomin', 16)}</button></div>`);

                $('#pvImg', viewer).src = source;

                let z = 1;

                $$('[data-z]', stage).forEach(b => {
                    b.onclick = () => {
                        const d = +b.dataset.z;

                        z = d === 0 ? 1 : Math.min(3, Math.max(0.5, z + d * 0.25));

                        $('#pvImg', viewer).style.transform = `scale(${z})`;
                        $('#zLbl', stage).textContent = Math.round(z * 100) + '%';
                    };
                });
            };

            image.onerror = () => failed('Не удалось показать изображение', 'файл не дошёл или повреждён');
            image.src = source;

            return;
        }

        if (item.kind === 'pdf') {
            // PDF показывает сам браузер. Это удобно, но и уязвимо:
            // встроенный просмотрщик можно отключить групповой политикой,
            // и тогда окно остаётся пустым БЕЗ ошибки. Поэтому ждём события
            // «загрузилось», а если его нет — предлагаем открыть отдельно.
            const frame = document.createElement('iframe');

            frame.title = item.name;
            frame.className = 'pv-frame';

            let shown = false;

            const giveUp = setTimeout(() => {
                if (!shown) {
                    failed('Не удалось показать PDF во встроенном окне',
                        'возможно, просмотр PDF отключён настройками браузера');
                }
            }, WAIT_MS);

            frame.addEventListener('load', () => { shown = true; clearTimeout(giveUp); });

            // Адрес задаём ДО вставки в страницу: пустой iframe, попав
            // в разметку, сразу выдаёт событие «загрузилось» про пустую
            // страницу, и мы бы приняли его за настоящее.
            frame.src = source;

            // Просмотрщику PDF нужна ВСЯ высота окна. Обычные отступы
            // и выравнивание по верху, годные для страницы документа,
            // превратили бы его в маленькое окошко внутри большого.
            viewer.classList.add('pv-viewer--frame');
            viewer.innerHTML = '';
            viewer.appendChild(frame);

            return;
        }

        if (item.kind === 'office') {
            html(item.doc || ('/Files?handler=OfficePreview&fileId=' + item.file), markup => {
                // Книга Excel шире окна, и прокручивает её само окно
                // предпросмотра — одной полосой внизу, вместо своей полосы
                // у каждого листа. Но окно выравнивает содержимое по центру,
                // а то, что шире центрирующего его поля, вылезает за края
                // С ОБЕИХ сторон — и до левого края потом не доехать никакой
                // прокруткой. Поэтому книге выравнивание переключается
                // на «от левого края».
                if (markup.indexOf('doc--book') >= 0) {
                    viewer.classList.add('pv-viewer--book');
                }

                // Масштаб. Нужен прежде всего книгам Excel: лист на двадцать
                // столбцов не помещается в окно ни при какой ширине экрана,
                // и единственный способ увидеть его целиком — отдалить.
                // Документам Word он тоже не мешает: увеличить мелкий скан
                // договора хотят не реже.
                return markup;
            }, () => {
                const doc = $('.doc', viewer);

                if (!doc) { return; }

                const stage = zoomBar('<div class="zoom-bar zoom-bar--doc">' +
                    `<button class="icon-btn" type="button" data-dz="-1" title="Отдалить">${ic('zoomout', 16)}</button>` +
                    '<button class="icon-btn zoom-val" type="button" data-dz="0" id="dzLbl" title="Вернуть 100%">100%</button>' +
                    `<button class="icon-btn" type="button" data-dz="1" title="Приблизить">${ic('zoomin', 16)}</button></div>`);


                let z = 1;

                const apply = () => {
                    // Масштабируем преобразованием, а не размером шрифта:
                    // размер шрифта переверстал бы таблицу заново, и столбцы
                    // разъехались бы относительно исходного документа.
                    // Точка отсчёта — левый верхний угол, иначе при отдалении
                    // содержимое уезжает от начала прокрутки.
                    doc.style.transform = z === 1 ? '' : `scale(${z})`;
                    doc.style.transformOrigin = 'top left';

                    // Место под уменьшенным документом иначе осталось бы
                    // занятым: преобразование не меняет размеров в разметке.
                    doc.style.width = z === 1 ? '' : (100 / z) + '%';

                    $('#dzLbl', stage).textContent = Math.round(z * 100) + '%';
                };

                $$('[data-dz]', stage).forEach(b => {
                    b.onclick = () => {
                        const d = +b.dataset.dz;

                        z = d === 0 ? 1 : Math.min(2, Math.max(0.4, z + d * 0.1));

                        apply();
                    };
                });
            });

            return;
        }

        if (item.kind === 'archive') {
            html(item.doc || ('/Files?handler=ArchivePreview&fileId=' + item.file));
            return;
        }

        if (item.kind === 'text') {
            html(source, text => '<div class="code-box">' +
                text.split('\n').map((line, i) =>
                    `<div><span class="ln">${String(i + 1).padStart(3, ' ')}</span>${esc(line)}</div>`).join('') +
                '</div>');

            return;
        }

        if (item.kind === 'video' || item.kind === 'audio') {
            // Проигрывает сам браузер. Файл идёт кусками (сервер отдаёт его
            // с поддержкой диапазонов), поэтому запись на сотни мегабайт
            // начинает играть сразу — важно на канале между офисами.
            const player = document.createElement(item.kind);

            player.controls = true;
            player.preload = 'metadata';
            player.className = 'player-media player-media--' + item.kind;
            player.src = source;

            player.addEventListener('error', () =>
                failed('Не удалось проиграть запись', 'браузер не понимает этот формат — скачайте файл'));

            if (item.kind === 'video') {
                viewer.classList.add('pv-viewer--frame');

                viewer.innerHTML = '';
                viewer.appendChild(player);

                return;
            }

            // У звука показывать нечего: браузер рисует голую полосу
            // проигрывания посреди пустого окна, и по ней не понять даже,
            // идёт ли воспроизведение вообще — на записи с тишиной в начале
            // это выглядит как «не работает». Поэтому вокруг полосы —
            // карточка с именем файла и полосками, которые пляшут, пока
            // запись играет, и замирают на паузе.
            //
            // Полоски рисуются стилями, а не по звуку: разбор звука
            // (Web Audio) требует читать содержимое файла в странице,
            // и ради украшения этого делать не стоит.
            viewer.innerHTML =
                '<div class="au-card" id="auCard">' +
                '<div class="au-eq" aria-hidden="true">' +
                '<i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i>' +
                '</div>' +
                `<div class="au-name">${esc(item.name)}</div>` +
                '<div class="au-player"></div>' +
                '</div>';

            $('.au-player', viewer).appendChild(player);

            const card = $('#auCard', viewer);

            player.addEventListener('play', () => card.classList.add('playing'));
            player.addEventListener('pause', () => card.classList.remove('playing'));
            player.addEventListener('ended', () => card.classList.remove('playing'));

            return;
        }

        failed('Такой файл браузер показать не умеет',
            'нажмите «Скачать» — файл откроется в своей программе');
    }
})();
