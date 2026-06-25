# Fallout 76 Manager - Localization Guide

Thank you for wanting to help translate the Fallout 76 Manager!

## How to create a new translation:

1.  **Copy the Template**: Make a copy of `template-LANG.json` in the same folder.
2.  **Rename**: Rename the copy to match your language code (e.g., `pt-PT.json` for Portuguese (Portugal), `sv-SE.json` for Swedish, etc.).
3.  **Translate**: Open the file in a text editor and replace the English text on the **right** side of the colons with your translation.
    *   **Keep the keys** (the words on the left) exactly as they are.
    *   **Keep placeholders** like `{0}` or `{1}`. They will be replaced by names or numbers in the app.
4.  **Save as UTF-8**: Ensure you save the file with UTF-8 encoding to support special characters.

## Testing your translation:

1.  Place your `.json` file in the `Release/www/locales/` folder.
2.  Restart the application.
3.  Go to **Settings** and your new language should be detectable (if matched to system) or selectable if added to the internal language list (requires a code update to appear in the dropdown).

> [!TIP]
> To have your language officially added to the application dropdown, please send your translated JSON file to the developer!
