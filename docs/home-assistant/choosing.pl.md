# Którą drogę połączenia wybrać

HADA może rozmawiać z Home Assistant na trzy sposoby. Nie wykluczają się i żaden nie jest zły; różnią się tym, co trzeba mieć, i tym, co się dostaje. Ta strona pomaga wybrać.

*Drugi i trzeci sposób są nowe w wersji 1.4.0.*

## W skrócie

| | **MQTT** | **Połączenie bezpośrednie** | **Połączenie bezpośrednie z integracją HADA** |
|---|---|---|---|
| Co trzeba mieć | Broker MQTT, na przykład aplikację Mosquitto w Home Assistant | Nic poza Home Assistant | Integrację HADA, zainstalowaną w Home Assistant przez HACS |
| Czujniki (ekran, bateria, co jest odtwarzane, …) | Tak | Tak | Tak |
| Przyciski, przełączniki i suwaki do klikania w Home Assistant (blokada, głośność, wyciszenie, …) | Tak | **Nie** | Tak |
| Powiadomienie z tekstem | Tak | Tak | Tak |
| Powiadomienie z obrazkiem i przyciskami | Tak, ale zapisane jako JSON | Tak, jak na telefonie | Tak, jak na telefonie |
| Obraz z kamery w powiadomieniu | Nie | Tak | Tak |
| Szybkie akcje uruchamiają automatyzacje | Tak | Tak | Tak |
| Działa, gdy komputer jest poza domem | Tylko jeśli do brokera da się dostać z zewnątrz | Tak, przez zewnętrzny adres Home Assistant | Tak, przez zewnętrzny adres Home Assistant |
| Home Assistant od razu wie, że komputer zniknął | Tak | Tylko gdy HADA została zamknięta poprawnie | Tak |

We wszystkich trzech komputer jest w Home Assistant urządzeniem z encjami, które można przemianować i przypisać do pomieszczenia.

## Co działa którą drogą

*Druga i trzecia kolumna są nowe w wersji 1.4.0; wiersze oznaczone \* są nowe w wersji 1.5.0.*

HADA robi tyle, na ile pozwala dana droga. Jeśli czegoś nie da się nią przekazać, wynika to z Home Assistanta, a nie z ustawienia, którego trzeba szukać: tabela mówi, czego dotyczy, a strona **Encje** w HADA mówi to samo o połączeniach, które masz.

| | **MQTT** | **Połączenie bezpośrednie** | **Połączenie bezpośrednie z integracją HADA** |
|---|---|---|---|
| Czujniki i czujniki binarne | Encje | Encje | Encje |
| Przyciski, przełączniki, suwak głośności | Encje | Nie ma ich w Home Assistant; z automatyzacji, przez [odpalenie zdarzenia](websocket.md#commands), z tokenem administratora | Encje |
| Odtwarzacz multimediów jako jedna encja \* | Nie: Home Assistant nie ma takiej encji w MQTT. Są przyciski multimediów, głośność i czujnik odtwarzania | Nie; jest czujnik odtwarzania | Tak |
| Powiadomienie: tekst i tytuł | `notify.send_message` | `notify.mobile_app_…` | `notify.send_message` i `hada.notify` |
| Powiadomienie: obrazek, przyciski, tag, adres, przypięte, ciche | Tak, zapisane jako JSON w treści | Tak | Tak, przez `hada.notify` |
| Powiadomienie: obraz z kamery | Nie: wymaga zalogowania w Home Assistant | Tak | Tak |
| Naciśnięty przycisk powiadomienia | Temat `…/event/notification_action` | Zdarzenia `mobile_app_notification_action` i `hada_event` | Zdarzenie `hada_event` |
| Szybka akcja uruchamia automatyzację | Wyzwalacz urządzenia i temat | Zdarzenie `hada_event` | Encja `event` i zdarzenie `hada_event` |
| Encje niedostępne, gdy komputer zniknie | Od razu, jakkolwiek zniknął | Tylko gdy HADA została zamknięta poprawnie | Od razu, jakkolwiek zniknął |
| Usunięte lub wyłączone encje znikają | Tak | Zostają jako *niedostępne* | Tak |
| Wystarczy token zwykłego użytkownika | Nie dotyczy: użytkownik brokera | Tak; \* także dla szybkich akcji | Tak |
| Inny adres w sieci domowej \* | Nie dotyczy | Tak | Tak |
| Logowanie przez Home Assistant zamiast tokenu \* | Nie dotyczy: broker ma nazwę użytkownika i hasło | Tak | Tak |
| Jasność ekranu \*, głośność: odczyt i ustawianie | Tak | Odczyt; ustawianie tylko przez odpalenie zdarzenia | Tak |
| Kilka Home Assistantów naraz | Tak | Tak | Tak |

Dotychczasowy sposób połączenia bezpośredniego, **Jako stany**, jest pominięty: robi to, co druga kolumna, z czujnikami, które Home Assistant zapomina po restarcie, i wymaga do wszystkiego tokenu administratora.

## Co wybrać

**Mam już brokera MQTT.** Wybierz MQTT. Nie trzeba niczego instalować w Home Assistant, a dostajesz wszystko poza obrazem z kamery w powiadomieniu. Jeśli chcesz też powiadomień jak na telefonie, [dodaj połączenie bezpośrednie tylko dla nich](#mqtt-i-powiadomienia-jak-na-telefonie).

**Nie mam brokera i chcę tylko widzieć, co robi komputer.** Wybierz połączenie bezpośrednie. Wpisujesz adres Home Assistant i token dostępu, i czujniki oraz powiadomienia już są. Nic do instalowania.

**Nie mam brokera i chcę sterować komputerem z Home Assistant.** Wybierz połączenie bezpośrednie z integracją HADA. Instalacja przez HACS zajmuje kilka minut, a potem wszystko działa jak przy MQTT.

**Mój laptop często jest poza domem.** Wybierz jedno z dwóch połączeń bezpośrednich i podaj adres, pod którym Twój Home Assistant jest dostępny z zewnątrz.

## Trzy sposoby po kolei

### MQTT

Komputer i Home Assistant rozmawiają z brokerem, czyli małym programem, który przekazuje wiadomości. Wiele osób ma go już ze względu na Zigbee2MQTT albo inne urządzenia.

- Ustawiasz na stronie **Połączenia**, w części **MQTT**: adres brokera, nazwę użytkownika i hasło.
- Komputer sam pojawia się w Home Assistant.
- Zobacz [Silnik MQTT](mqtt.md).

### Połączenie bezpośrednie

HADA sama łączy się z Home Assistant, tak jak aplikacja Home Assistant na telefonie, i pojawia się w **Ustawienia → Urządzenia i usługi → Mobile App**.

- Ustawiasz na stronie **Połączenia**, w części **Home Assistant (WebSocket)**: adres Home Assistant i długoterminowy token dostępu. Zostaw **Powiadomienia** włączone, a **Encje** na *Jako encje urządzenia*.
- Powiadomienia wysyłasz akcją `notify.mobile_app_` z nazwą komputera, dokładnie tak jak na telefon.
- W Home Assistant nie ma przycisków ani przełączników komputera. Nadal da się nim sterować z automatyzacji, odpalając zdarzenie; zobacz [Polecenia](websocket.md#commands).
- Zobacz [Silnik WebSocket](websocket.md).

### Połączenie bezpośrednie z integracją HADA

To samo połączenie, ale w Home Assistant jest zainstalowana część HADA, [integracja HADA](https://github.com/inowakowski/hada-homeassistant), która umie pokazać przyciski, przełączniki i suwaki.

- W Home Assistant: zainstaluj integrację przez HACS i dodaj ją raz w **Ustawienia → Urządzenia i usługi**.
- W HADA: na stronie **Połączenia**, w części **Home Assistant (WebSocket)**, ustaw **Encje** na *Przez integrację HADA*.
- Powiadomienia wysyłasz akcją `hada.notify`.
- Zobacz [Encje przez integrację HADA](websocket.md#sensors-through-the-hada-integration).

## Kilka naraz

### MQTT i powiadomienia jak na telefonie

Zostaw MQTT dla czujników i sterowania, a obok dodaj połączenie bezpośrednie z włączonymi **Powiadomieniami** i **Encjami** ustawionymi na *Wyłączone*. Home Assistant dostaje wtedy encje raz, przez MQTT, a dodatkowo może wysyłać na komputer powiadomienia z obrazami z kamer.

### Czego nie robić

Nie wysyłaj encji dwiema drogami do tego samego Home Assistant, na przykład przez MQTT i przez integrację HADA: wszystko byłoby tam dwa razy. Którąkolwiek drogę wybierzesz dla encji, na drugiej ustaw **Encje** na *Wyłączone*.

## Zmiana później

Zdanie możesz zmienić w każdej chwili na stronie **Połączenia**. Pamiętaj tylko, że:

- Encje dostają w Home Assistant nowe nazwy, bo każda droga nazywa je trochę inaczej. Automatyzacje i dashboardy, które z nich korzystają, trzeba przestawić na nowe.
- Stare encje zostają w Home Assistant jako *niedostępne*, dopóki nie usuniesz tam starego urządzenia.
