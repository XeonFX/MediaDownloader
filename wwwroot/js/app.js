window.mediaDownloader = {
    getTheme: function () {
        return localStorage.getItem("md-theme");
    },
    setTheme: function (value) {
        localStorage.setItem("md-theme", value);
    },
    prefersDark: function () {
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    },
    requestNotificationPermission: function () {
        if ("Notification" in window && Notification.permission === "default") {
            Notification.requestPermission();
        }
    },
    showNotification: function (title, body) {
        if ("Notification" in window && Notification.permission === "granted") {
            new Notification(title, { body: body, icon: "favicon.png" });
        }
    }
};
