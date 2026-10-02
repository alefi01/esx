/*
 * Звук уведомления и всплывающие окошки Windows.
 *
 * ПОЧЕМУ ЗВУК СОБИРАЕТСЯ КОДОМ, А НЕ БЕРЁТСЯ ИЗ ФАЙЛА
 *
 * В вашей сети нет интернета, поэтому любой звуковой файл пришлось бы
 * положить в сам портал. Три-четыре сигнала — это сотни килобайт,
 * которые качает каждый браузер, плюс вечный вопрос, какой формат
 * проиграется везде. Здесь звук строится из нескольких чистых тонов
 * прямо в браузере: кода на десяток строк, вес нулевой, звучит
 * одинаково во всех браузерах.
 *
 * ПОЧЕМУ БРАУЗЕР МОЛЧИТ ДО ПЕРВОГО НАЖАТИЯ
 *
 * Браузеры запрещают сайтам играть звук, пока человек на странице
 * ничего не нажал, — иначе половина интернета начинала бы орать при
 * открытии. Это не обходится и обходить не нужно: достаточно один раз
 * нажать что-нибудь на странице (например, «Прослушать» в настройках),
 * и дальше звук работает до закрытия вкладки.
 */

(function () {
    'use strict';

    var context = null;

    function audio() {
        if (context) { return context; }

        var Ctor = window.AudioContext || window.webkitAudioContext;

        if (!Ctor) { return null; }

        try {
            context = new Ctor();
        } catch (error) {
            return null;
        }

        return context;
    }

    /**
     * Один тон: частота в герцах, когда начать и сколько длиться.
     *
     * Громкость нарастает и спадает плавно (setValueAtTime →
     * exponentialRampToValueAtTime). Резко включённый и выключенный тон
     * даёт щелчок на границе — именно он делает самодельные сигналы
     * похожими на неисправность.
     */
    function tone(ctx, frequency, startAt, duration, volume) {
        var osc = ctx.createOscillator();
        var gain = ctx.createGain();

        osc.type = 'sine';
        osc.frequency.value = frequency;

        gain.gain.setValueAtTime(0.0001, startAt);
        gain.gain.exponentialRampToValueAtTime(volume, startAt + 0.012);
        gain.gain.exponentialRampToValueAtTime(0.0001, startAt + duration);

        osc.connect(gain);
        gain.connect(ctx.destination);

        osc.start(startAt);
        osc.stop(startAt + duration + 0.02);
    }

    // Сигнал один: два коротких тона, негромких и без звона. Выбор
    // из нескольких мелодий — настройка, которую открывают один раз
    // из любопытства, а место в интерфейсе она занимает постоянно.
    var VOICES = {
        soft: [[660, 0, 0.16, 0.10], [880, 0.09, 0.20, 0.08]]
    };

    // ------------------------------------------------------------------
    // Разрешение на звук.
    //
    // Браузер не даёт сайту играть, пока человек на странице ничего
    // не нажал. Проверяется это один раз: достаточно любого нажатия —
    // по ссылке, по кнопке, по пустому месту, — и звук работает
    // до закрытия вкладки.
    //
    // Поэтому мы заводим звуковой движок при ПЕРВОМ же нажатии, не дожидаясь
    // самого сигнала. Иначе первое сообщение за день приходило бы молча:
    // к моменту его прихода нажатий ещё не было, и браузер отказывал.
    //
    // Это единственное, что нужно для звука. HTTPS ему не требуется —
    // в отличие от окошек системы (см. ниже).
    // ------------------------------------------------------------------

    function unlock() {
        var ctx = audio();

        if (!ctx) { return; }

        if (ctx.state === 'suspended') {
            try { ctx.resume(); } catch (error) { /* не вышло — попробуем в другой раз */ }
        }
    }

    ['pointerdown', 'keydown'].forEach(function (event) {
        document.addEventListener(event, unlock, { once: false, passive: true });
    });

    function play(name) {
        var voice = VOICES[name];

        if (!voice) { return; }

        var ctx = audio();

        if (!ctx) { return; }

        // Браузер мог усыпить звук, пока вкладка была свёрнута.
        if (ctx.state === 'suspended') {
            try { ctx.resume(); } catch (error) { /* не вышло — просто промолчим */ }
        }

        var now = ctx.currentTime + 0.01;

        voice.forEach(function (part) {
            tone(ctx, part[0], now + part[1], part[2], part[3]);
        });
    }

    // ------------------------------------------------------------------
    // Всплывающие окошки самой системы.
    //
    // Работают, пока вкладка с порталом открыта (можно свёрнутую),
    // и только если человек разрешил их в браузере. Показываем ТОЛЬКО
    // когда портал не на виду: если человек и так смотрит в переписку,
    // окошко поверх неё — помеха, а не помощь.
    // ------------------------------------------------------------------

    function canNotify() {
        return typeof Notification !== 'undefined' && Notification.permission === 'granted';
    }

    function notify(title, text, url) {
        if (!canNotify() || document.visibilityState === 'visible') { return; }

        try {
            var shown = new Notification(title, {
                body: text,
                icon: '/favicon.ico',

                // tag заменяет предыдущее окошко вместо того, чтобы копить
                // их стопкой: десять сообщений подряд — это одно событие
                // «вам пишут», а не десять окон поверх рабочей программы.
                tag: 'portal'
            });

            shown.onclick = function () {
                window.focus();

                if (url) { window.location.href = url; }

                shown.close();
            };
        } catch (error) {
            // Некоторые браузеры запрещают это вне защищённого соединения.
        }
    }

    function ask() {
        if (typeof Notification === 'undefined') {
            return Promise.resolve('unsupported');
        }

        // По открытому соединению спрашивать бесполезно: браузер ответит
        // отказом, не показав человеку никакого вопроса.
        if (!window.isSecureContext) {
            return Promise.resolve('insecure');
        }

        if (Notification.permission !== 'default') {
            return Promise.resolve(Notification.permission);
        }

        try {
            return Notification.requestPermission();
        } catch (error) {
            return Promise.resolve(Notification.permission);
        }
    }

    // ------------------------------------------------------------------
    // Заметность без окошек системы: заголовок вкладки и значок на ней.
    //
    // ЗАЧЕМ. Окошки Windows требуют защищённого соединения, и по открытому
    // HTTP браузер отказывает в них молча. Но свёрнутый портал всё равно
    // должен уметь позвать: человек работает в другой программе, а в панели
    // задач у него висит вкладка.
    //
    // Поэтому при непрочитанном заголовок вкладки мигает, а на значке
    // появляется число. В панели задач Windows это видно так же хорошо,
    // как всплывающее окошко, и работает везде — хоть по HTTP, хоть
    // без всяких разрешений.
    //
    // Мигание только пока вкладка НЕ на виду: человеку, который смотрит
    // на портал, дёргать заголовок незачем — он и так всё видит.
    // ------------------------------------------------------------------

    var titleOriginal = document.title;
    var titleTimer = null;
    var titleSwapped = false;
    var iconOriginal = null;

    function iconLink() {
        var link = document.querySelector('link[rel~="icon"]');

        if (!link) {
            link = document.createElement('link');
            link.rel = 'icon';
            document.head.appendChild(link);
        }

        if (iconOriginal === null) { iconOriginal = link.getAttribute('href') || ''; }

        return link;
    }

    /**
     * Значок вкладки с числом непрочитанного.
     *
     * Рисуется прямо в браузере, а не берётся готовой картинкой: иначе
     * пришлось бы держать в портале по картинке на каждое число.
     */
    function paintIcon(count) {
        var link = iconLink();

        if (!count) {
            link.setAttribute('href', iconOriginal || '/favicon.ico');
            return;
        }

        try {
            var size = 32;
            var canvas = document.createElement('canvas');

            canvas.width = size;
            canvas.height = size;

            var g = canvas.getContext('2d');

            // Скруглённый квадрат цвета портала и число поверх. Своя
            // отрисовка вместо наложения на родной значок: родной лежит
            // в формате ICO, и не всякий браузер умеет рисовать его
            // на холсте — а молча получить пустой значок хуже, чем
            // нарисовать свой.
            g.fillStyle = '#0067c0';
            g.beginPath();
            g.roundRect ? g.roundRect(0, 0, size, size, 7) : g.rect(0, 0, size, size);
            g.fill();

            g.fillStyle = '#ffffff';
            g.textAlign = 'center';
            g.textBaseline = 'middle';

            var text = count > 9 ? '9+' : String(count);

            g.font = 'bold ' + (count > 9 ? 17 : 22) + 'px system-ui, sans-serif';
            g.fillText(text, size / 2, size / 2 + 1);

            link.setAttribute('href', canvas.toDataURL('image/png'));
        } catch (error) {
            // Холст недоступен — обойдёмся мигающим заголовком.
        }
    }

    function stopBlink() {
        if (titleTimer !== null) {
            clearInterval(titleTimer);
            titleTimer = null;
        }

        if (titleSwapped) {
            document.title = titleOriginal;
            titleSwapped = false;
        }
    }

    function blink(count) {
        stopBlink();

        if (!count || document.visibilityState === 'visible') { return; }

        var alert = '(' + count + ') Новое — ' + titleOriginal;

        document.title = alert;
        titleSwapped = true;

        titleTimer = setInterval(function () {
            titleSwapped = !titleSwapped;
            document.title = titleSwapped ? alert : titleOriginal;
        }, 1400);
    }

    // Вернулись на вкладку — мигание прекращается сразу, не дожидаясь
    // следующей проверки: человек уже здесь.
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') { stopBlink(); }
    });

    window.portalSound = {
        play: play,
        notify: notify,
        ask: ask,

        /**
         * Сколько непрочитанного — отражается на вкладке: число на значке
         * и мигающий заголовок, пока вкладка не на виду.
         */
        unread: function (count) {
            paintIcon(count);
            blink(count);
        },

        /**
         * Что с окошками системы.
         *
         * Отдельно выделен случай «нужен HTTPS»: по открытому соединению
         * браузер не отказывает в ответ на запрос, а сразу отвечает
         * «запрещено», — и человек идёт искать, где он это запретил,
         * хотя запрещал не он.
         */
        state: function () {
            if (typeof Notification === 'undefined') { return 'unsupported'; }

            if (!window.isSecureContext) { return 'insecure'; }

            return Notification.permission;
        }
    };
})();
