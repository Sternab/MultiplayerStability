Placeholder so the built mod ships a Blueprints folder. The OwlcatModification loader throws
DirectoryNotFoundException at apply time when the installed mod has no Blueprints directory
(harmless for a code-only mod, but noisy in GameLogFull). If a build still omits the folder,
create an empty "Blueprints" folder inside the installed mod directory.
