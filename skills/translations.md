# Translations

How this plugin shows its text in the viewer's language. Base builds the strings in and checks them on every build. The full reference is Base's [Translations](https://jpkribs.github.io/jellyfin-plugin-base/Translations/) guide.

## Where the Strings Live

| Strings | Where |
| :--- | :--- |
| Jellyfin's own | `<JellyfinString Include="Key" />` items in `Jellyfin.Plugin.ServerSync/Jellyfin.Plugin.ServerSync.csproj` |
| This plugin's own | `Jellyfin.Plugin.ServerSync/Localization/en-us.json`, one file per language |
| The endpoint pages load them from | `GET /Sync/Translations`, which answers with `BaseTranslations.Serve` and allows anonymous callers |
| The check that every key a page uses exists | `Jellyfin.Plugin.ServerSync.Tests/Localization/TranslationKeyTests.cs` |

## Rules

* Every word a person reads comes from a key. No hard coded English in a page, a script, or a message the server shows.
* Use a Jellyfin string whenever Jellyfin has the words. Search the web client's `src/strings/en-us.json` and the server's `Localization/Core/en-US.json` before adding a key.
* Add a local key only when Jellyfin has no string for it. Put it in `en-us.json` with its US English.
* One string, one key. The build fails on a key said twice or English said twice.
* Keep placeholders as `{0}`, `{1}`. Never build a sentence from pieces, since word order differs by language.
* A count takes two keys, one for exactly one and one for any other count, such as `ToastItemsIgnoredOne` and `ToastItemsIgnoredMany`.
* Name a key in a page literally, as `tr('Key')`, so the key test sees it. A map of keys is fine when a value picks the text.
* A key is PascalCase and says what the text is, such as `HeaderQueue`, `LabelApiKey`, `ButtonRetry`, `ToastSaved`, `ErrorServerUnreachable`.
* Log lines and exception text for admins in the log stay in English and need no key.

## Add a String

1. Search Jellyfin's strings for the words.
2. Found: add `<JellyfinString Include="Key" />` to the csproj.
3. Not found: add `"Key": "English"` to `Localization/en-us.json`.
4. Use it.
    * Markup: `data-str="Key"`, `data-str-title`, `data-str-label`, or `data-str-placeholder`, with the English as the element's own text.
    * Script: `shared.t.tr('Key')`, `shared.t.format('Key', value)`, or `shared.plural(count, 'KeyOne', 'KeyMany')`. `serversync_shared.js` loads the strings once with `loadTranslations('Sync/Translations')`, and a page's `bind` and `load` and the Sync tab's modules run only after they arrive.
    * Tab names: `tabName('Key', 'English')` in `serversync_shared.js`, since the tabs show before the strings load.
    * C#: `BaseTranslations.Get(TranslationKeys.Key, language)` or `BaseTranslations.Format(...)`. Text a route returns to a page uses `BaseTranslations.RequestLanguage(Request)`, and `BaseTranslations.UseLanguage` puts Base's own text, such as an SSRF refusal, in that language. Text for everyone, such as an activity log entry, uses `BaseTranslations.ServerLanguage(config)`.
5. Build. Fix every `JPKT` error and warning.
6. Run `dotnet test` for the key test, and `node --test`, whose support serves the US English strings to the pages.

## Add a Language

Add `Localization/<code>.json`, such as `de.json` or `pt-br.json`, with only keys `en-us.json` has. A missing key falls back to the language's base and then to US English. Jellyfin's strings come in every language Jellyfin has, with no file.

## Checklist

* [ ] No English in a page, script, or server message outside `Localization/en-us.json`.
* [ ] Jellyfin's string used wherever it has the words.
* [ ] The build has no `JPKT` error or warning.
* [ ] The pages show the strings, checked once in another language such as `de`.
