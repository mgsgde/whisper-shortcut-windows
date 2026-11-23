Fehlerbehebung für Version 0.7.13:

- **Diktieren funktioniert wieder:** Seit 0.7.13 kam beim Diktieren über Gemini oft nur ein einzelnes Wort oder Unsinn heraus. Ursache war die Erkennung von Sprechpausen, die fast die ganze Aufnahme abgeschnitten hat. Jetzt geht wieder das ganze Diktat an Gemini.
- **Offline-Spracherkennung (Parakeet) lässt sich wieder laden:** Der Download brach mit „Download beschädigt“ ab, weil der Anbieter die Modelldateien ausgetauscht hatte. WhisperShortcut lädt jetzt fest die geprüfte Fassung. Wer Parakeet schon installiert hat, muss nichts tun.

