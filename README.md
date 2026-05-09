# A fork of Subtitle Edit by Nikse
It just contains some extra hacks that are probably not something that everybody wants.

See the original project and support the developer: https://github.com/SubtitleEdit/subtitleedit

There will be no releases of this fork.

# Changes
I usually take pride in what I do, and these changes are more a proof of concept kind of thing. I don't have the availability to do them in a well thought out manner right this minute. As soon as the new update settles I'll see about creating pull requests for some of the things. If you want to steal my changes and create your own pull-requests: Be my guest. Just don't associate them with me.

- Progress in the title when loading a MKV (it looks like something sensible will be coming in the original project)
- I don't understand the Save-Button so it's Save-As for me. (Hack)
- User-Defined words are not flagged as misspellings in the OCR-module (The spellchecking is complex and handles many edge-cases. I am worried I broke more for other users than fixed for me)
- Words added to the dictionary are not flagged as misspellings. (Hack, I just add them to the skip-word-list)
- Ollama now works with the generate endpoint