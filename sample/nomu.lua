-- nomsidian の設定ファイル(WezTerm の wezterm.lua に倣った書き方)。
-- vault 直下に置くとそのvault専用の設定になる。
-- グローバルの `nomu` テーブルはホスト側(C#)が注入するAPIで、
-- nomu.on / nomu.action は将来のプラグイン機構向けの器(現時点では未発火)。

local config = {}

config.theme = {
    editor_bg = "#12141a",
    sidebar_bg = "#0e1016",
    panel_bg = "#181b22",
    border = "#2a2e3a",
    text = "#e3e6ee",
    muted_text = "#7d8494",
    accent = "#57c7ff",
    accent_muted = "#2c5570",
    hover = "#232733",
}

config.font = {
    family = "Yu Gothic UI",
    size = 12,
}

config.editor = {
    vim_mode = true,
}

config.statusline = {
    mode_colors = {
        normal = "#4caf50",
        insert = "#4a90d9",
        visual = "#b388ff",
        replace = "#e06c75",
    },
}

return config
