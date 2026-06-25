export const UI_THEMES = [
    { id: 'fallout', labelKey: 'theme_fallout', logo: 'assets/themes/fallout.png', logoLayout: {"width":44,"height":0,"scale":1.0,"offsetX":0,"offsetY":0,"objectFit":"cover","objectPosition":"center bottom","opacity":1,"shadowY":4,"shadowBlur":12,"shadowOpacity":0.5,"collapsedScale":1.0,"collapsedOffsetX":0} },
    { id: 'vault-tec', labelKey: 'theme_vault_tec', logo: 'assets/themes/vault-tec.png', logoLayout: {"width":44,"height":0,"scale":1.0,"offsetX":0,"offsetY":0,"objectFit":"cover","objectPosition":"center bottom","opacity":1,"shadowY":4,"shadowBlur":12,"shadowOpacity":0.5,"collapsedScale":1.0,"collapsedOffsetX":0} },
    { id: 'red-black', labelKey: 'theme_red_black', logo: 'assets/themes/red-black.png', logoLayout: {"width":44,"height":0,"scale":1.0,"offsetX":0,"offsetY":0,"objectFit":"cover","objectPosition":"center bottom","opacity":1,"shadowY":4,"shadowBlur":12,"shadowOpacity":0.5,"collapsedScale":1.0,"collapsedOffsetX":0} },
    { id: 'black-white', labelKey: 'theme_black_white', logo: 'assets/themes/black-white.webp', logoLayout: {"width":44,"height":0,"scale":1.0,"offsetX":0,"offsetY":0,"objectFit":"cover","objectPosition":"center bottom","opacity":1,"shadowY":4,"shadowBlur":12,"shadowOpacity":0.5,"collapsedScale":1.0,"collapsedOffsetX":0} },
];

export const DEFAULT_UI_THEME = 'fallout';

const THEME_IDS = new Set(UI_THEMES.map((t) => t.id));

export function isBuiltInUiTheme(id) {
    return typeof id === 'string' && THEME_IDS.has(id);
}
