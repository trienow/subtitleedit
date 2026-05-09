# A fork of Subtitle Edit by Nikse
It just contains some extra hacks that are probably not something that everybody wants.

See the original project and support the developer: https://github.com/SubtitleEdit/subtitleedit

There will be no releases of this fork.

The version this is based on is still in-dev and far from done with code changing a lot, so I won't be making pull requests in the near future.

# Changes
- Progress in the title when loading a MKV (it looks like something sensible will be coming in the original project)
- I don't understand the Save-Button so it's Save-As for me. (Hack)
- User-Defined words are not flagged as misspellings in the OCR-module (The spellchecking is complex and handles many edge-cases. I am worried I broke more for other users than fixed for me)
- Words added to the dictionary are not flagged as misspellings. (Hack, I just add them to the skip-word-list)
- Ollama now works with the generate endpoint (Hard design change)
- User-Defined RegularExpressionsIfSpelledCorrectly are loaded (No idea, why this isn't done by default...)