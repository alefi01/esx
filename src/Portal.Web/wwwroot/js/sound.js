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

        if (Notification.permission !== 'default') {
            return Promise.resolve(Notification.permission);
        }

        try {
            return Notification.requestPermission();
        } catch (error) {
            return Promise.resolve(Notification.permission);
        }
    }

    window.portalSound = {
        play: play,
        notify: notify,
        ask: ask,

        /** Поддерживает ли браузер окошки и что с разрешением. */
        state: function () {
            if (typeof Notification === 'undefined') { return 'unsupported'; }

            return Notification.permission;
        }
    };
})();
