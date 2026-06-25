export class Translator {
    constructor() {
        this.currentLanguage = 'en-US';
        this.loadedLanguages = {};
        this.fallbackLanguage = 'en-US';
        this.pendingRequests = {};
    }

    async init() {
        try {
            await this.loadLanguage(this.fallbackLanguage);
        } catch (e) {
            console.error("[Translator] Critical: Failed to load fallback language.", e);
        }
        
        const detected = this.detectLanguage();
        if (detected !== this.fallbackLanguage) {
             try {
                 await this.loadLanguage(detected);
                 this.currentLanguage = detected;
             } catch (e) {
                 console.warn("[Translator] Failed to load detected language, sticking to fallback.", e);
             }
        }
    }

    async setLanguage(lang) {
        if (!lang) return;
        if (lang === 'auto') {
            lang = this.detectLanguage();
        }
        
        if (this.currentLanguage === lang && this.loadedLanguages[lang]) return;

        console.log(`[Translator] Switching to ${lang}...`);
        
        try {
            await this.loadLanguage(lang);
            this.currentLanguage = lang;
            console.log(`[Translator] Language set to ${lang}`);
        } catch (e) {
            console.error(`[Translator] Failed to load ${lang}, falling back to ${this.fallbackLanguage}`);
            this.currentLanguage = this.fallbackLanguage;
        }
    }

    loadLanguage(lang) {
        if (this.loadedLanguages[lang]) return Promise.resolve(this.loadedLanguages[lang]);
        
        
        return new Promise((resolve, reject) => {
            this.pendingRequests[lang] = { resolve, reject };
            
            if (window.chrome && window.chrome.webview) {
                console.log(`[Translator] Requesting ${lang} from backend...`);
                
                let attempts = 0;
                const maxAttempts = 10;
                
                window.chrome.webview.postMessage({ type: 'LOAD_LANGUAGE', lang: lang });
                
                const interval = setInterval(() => {
                    if (!this.pendingRequests[lang]) {
                        clearInterval(interval);
                        return;
                    }
                    
                    attempts++;
                    if (attempts >= maxAttempts) {
                        clearInterval(interval);
                        if (this.pendingRequests[lang]) {
                             console.warn(`[Translator] Timeout loading ${lang} after ${maxAttempts} attempts`);
                             delete this.pendingRequests[lang];
                             reject(new Error("Timeout"));
                        }
                    } else {
                        console.log(`[Translator] Retry ${attempts}/${maxAttempts} for ${lang}...`);
                        window.chrome.webview.postMessage({ type: 'LOAD_LANGUAGE', lang: lang });
                    }
                }, 500);
            } else {
                console.warn("[Translator] No backend detected, trying fetch fallback...");
                fetch(`locales/${lang}.json`)
                    .then(r => r.json())
                    .then(json => {
                        this.loadedLanguages[lang] = json;
                        delete this.pendingRequests[lang];
                        resolve(json);
                    })
                    .catch(reject);
            }
        });
    }

    receiveLanguageContent(lang, content, error) {
        const req = this.pendingRequests[lang];
        if (!req) return;
        
        if (error) {
            console.error(`[Translator] Backend reported error for ${lang}:`, error);
            req.reject(new Error(error));
        } else {
            this.loadedLanguages[lang] = content;
            req.resolve(content);
        }
        delete this.pendingRequests[lang];
    }

    detectLanguage() {
        const navLang = navigator.language || navigator.userLanguage || 'en-US';
        if (navLang.startsWith('en')) return 'en-US';
        if (navLang.startsWith('ja')) return 'ja-JP';
        if (navLang.startsWith('fr')) return 'fr-FR';
        if (navLang.startsWith('de')) return 'de-DE';
        if (navLang.startsWith('es')) return 'es-ES';
        if (navLang.startsWith('it')) return 'it-IT';
        if (navLang.startsWith('pl')) return 'pl-PL';
        if (navLang.startsWith('ru')) return 'ru-RU';
        if (navLang.startsWith('pt')) return 'pt-BR';
        if (navLang.startsWith('zh')) return navLang.toLowerCase().includes('tw') || navLang.toLowerCase().includes('hk') ? 'zh-TW' : 'zh-CN';
        if (navLang.startsWith('ko')) return 'ko-KR';
        return 'en-US';
    }

    t(key, ...args) {
        let text = null;
        if (this.loadedLanguages[this.currentLanguage]) {
            text = this.loadedLanguages[this.currentLanguage][key];
        }

        if (!text && this.loadedLanguages[this.fallbackLanguage]) {
            text = this.loadedLanguages[this.fallbackLanguage][key];
        }

        if (!text) text = key;

        if (args.length > 0) {
            args.forEach((arg, i) => {
                // Replace ALL occurrences of the placeholder (split/join avoids `$` regex pitfalls).
                text = text.split(`{${i}}`).join(String(arg));
            });
        }
        return text;
    }
}

export const translator = new Translator();
