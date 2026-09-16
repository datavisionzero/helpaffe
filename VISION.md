# helpaffe — Produktvision

> Status: Produktvision mit beschlossenem Ticketmodell, Agenten-Arbeitsablauf,
> MVP-Umfang und davon getrennter Roadmap.
> Dieses Dokument beschreibt das Zielbild, keinen
> bereits implementierten Funktionsumfang. Es entsteht zunächst auf Deutsch
> und wird später für das öffentliche Repository ins Englische übersetzt.

## 1. Die Idee

helpaffe ist ein selbst hostbares Helpdesk-System für Menschen und Teams, die
den Support für mehrere Produkte gemeinsam betreuen. Tickets, Bearbeitung und
Benachrichtigungen laufen an einer zentralen Stelle zusammen. Jedes angebundene
Produkt behält dabei seine eigene Oberfläche und seinen eigenen Auftritt
gegenüber seinen Endnutzern.

Support-Mitarbeiter arbeiten in einer Weboberfläche oder lassen ihre KI-Agenten
über eine eigens dafür entwickelte CLI arbeiten. Die CLI ist ein zentraler
Bestandteil des Produkts: Bis auf wenige besonders weitreichende
Administrationstätigkeiten muss der gesamte Funktionsumfang darüber zugänglich
sein. Das schließt die Einrichtung und Pflege von E-Mail-Templates ein.

Oberfläche, Bedienkonzept und technische Grundlage orientieren sich eng an
[planaffe](https://github.com/datavisionzero/planaffe) und
[hostingaffe](https://github.com/datavisionzero/hostingaffe).

## 2. Für wen wir das bauen

helpaffe richtet sich an Produktentwickler und Support-Teams, die mehrere
Produkte betreiben oder betreuen und deren Support zentral organisieren wollen.
Der Hauptanwendungsfall ist ein Administrator, der zugleich den Support
verantwortet und alle Fälle gemeinsam mit seinem KI-Agenten bearbeitet.
Weitere Support-Mitarbeiter nutzen dasselbe Modell ohne zusätzliche
Freigabeschritte.

Die angebundenen Produkte können beliebige Anwendungen sein. Sie müssen weder
zur affe-Produktfamilie gehören noch denselben technischen Aufbau haben.
Voraussetzung ist lediglich, dass sie die Helpdesk-API direkt oder über ein
von uns bereitgestelltes SDK integrieren können.

## 3. Das Problem

Wer mehrere Produkte betreibt, braucht in jedem davon eine Möglichkeit für
Endnutzer, Support-Anfragen zu stellen. Ohne einen gemeinsamen Helpdesk entstehen
mehrere getrennte Backoffices: Tickets, Zuständigkeiten und Kommunikation müssen
an verschiedenen Stellen bearbeitet werden. Dieselben administrativen Funktionen
werden für jedes Produkt erneut gebaut und gepflegt.

Gleichzeitig reicht eine auf Menschen ausgelegte Weboberfläche nicht aus, wenn
Support-Mitarbeiter ihre Arbeit an KI-Agenten delegieren möchten. Agenten brauchen
einen vollständigen, verlässlichen Zugang zu den Arbeitsabläufen und zur
alltäglichen Konfiguration des Helpdesks.

helpaffe bündelt diese Arbeit, während der Support aus Sicht der Endnutzer
weiterhin zu dem Produkt gehört, das sie verwenden.

## 4. Produkt und Helpdesk haben klare Aufgaben

### Das angebundene Produkt

Das Produkt stellt die Oberfläche bereit, in der seine Endnutzer Tickets
anlegen, ihre Tickets ansehen und die Unterhaltung mit dem Support fortsetzen.
Es integriert unser SDK beziehungsweise unsere API, um diese Vorgänge an
helpaffe zu übermitteln und die benötigten Ticketdaten abzurufen.

Die Gestaltung und Einbettung dieser Oberfläche bleiben beim Produkt.
Endnutzer sollen für ihren Support nicht in ein separates helpaffe-Backoffice
wechseln müssen.

### helpaffe

helpaffe stellt das zentrale Backoffice bereit. Hier bearbeiten
Support-Mitarbeiter und ihre Agenten die Tickets aller betreuten Produkte.
Hier liegen auch die produktbezogene Support-Konfiguration und der Versand
der E-Mail-Benachrichtigungen.

SDK und API bilden die Integrationsgrenze. Das angebundene Produkt muss keine
eigene Support-Administration und keinen eigenen Versand von
Helpdesk-Benachrichtigungen implementieren.

### Produktanbindung und Endnutzer-Identität

Die Integration erfolgt über das Backend des angebundenen Produkts. Dieses
spricht die helpaffe-API direkt oder über das SDK an. Geheime Produktschlüssel
bleiben im Backend und werden niemals an das Browser-Frontend ausgeliefert.

Jedes Projekt erhält eigene API-Schlüssel für die Produktintegration.
Schlüssel sind einzeln widerrufbar; mehrere gleichzeitig gültige Schlüssel
ermöglichen einen Austausch ohne Unterbrechung. Sie berechtigen ausschließlich
zur Produktintegration und gewähren keinen Zugriff auf interne Notizen oder
Administration.

Ein Endnutzer wird durch die Kombination aus Projekt und stabiler Benutzer-ID
des angebundenen Produkts identifiziert. Name und E-Mail-Adresse werden
zusätzlich übertragen. Eine Änderung der E-Mail-Adresse erzeugt keine neue
Endnutzer-Identität.

Das Produkt authentifiziert seinen Endnutzer und übermittelt dessen ID bei
Integrationszugriffen. helpaffe beschränkt diese Zugriffe auf die Tickets
dieses Endnutzers innerhalb des zum API-Schlüssel gehörenden Projekts.
Dabei vertraut helpaffe dem Produkt-Backend, die richtige Endnutzer-ID zu
übermitteln; die Benutzeranmeldung des Produkts bleibt dessen Verantwortung.

Endnutzer benötigen kein helpaffe-Konto. Im MVP werden ausschließlich
identifizierte Benutzer des angebundenen Produkts unterstützt. Anonyme
Anfragen beziehungsweise anonyme Kontaktformulare gehören nicht zum MVP.

Das erste SDK ist ein serverseitiges .NET-SDK. Andere Backends können die
HTTP-API unmittelbar verwenden. Weitere SDK-Sprachen werden bei konkretem
Bedarf ergänzt.

### Ein typischer Ablauf

1. Ein Endnutzer erstellt in Produkt A ein Ticket.
2. Produkt A übermittelt das Ticket über SDK oder API an helpaffe. Dort wird es
   dem Projekt für Produkt A zugeordnet.
3. helpaffe benachrichtigt die zuständigen Support-Mitarbeiter per E-Mail.
4. Ein Support-Mitarbeiter oder sein KI-Agent bearbeitet das Ticket im zentralen
   Helpdesk und verfasst eine Antwort.
5. helpaffe benachrichtigt den Endnutzer mit Absender und E-Mail-Vorlage von
   Produkt A. Die Unterhaltung ist über die Integration auch in Produkt A
   zugänglich.

Für diese Benachrichtigungen ist keine zusätzliche Schnittstelle zurück zum
Produkt notwendig: helpaffe verschickt die E-Mails selbst.

## 5. Mehrere Produkte, ein gemeinsamer Support

Ein **Projekt** ist in helpaffe der organisatorische Rahmen für ein
angebundenes Produkt. Jedes Ticket gehört eindeutig zu einem Projekt.

Ein Benutzer und sein Team können mehrere Projekte betreuen. Die tägliche
Arbeit muss sowohl projektübergreifend als auch auf ein einzelnes Projekt
beschränkt möglich sein. Dabei muss stets erkennbar sein, zu welchem Produkt
ein Ticket gehört und in dessen Namen kommuniziert wird.

Die gemeinsame Verwaltung darf die Produkte gegenüber ihren Endnutzern nicht
vermischen. Die Integration darf nur auf die jeweils berechtigten Projekte
und Endnutzer-Tickets zugreifen. Ein Endnutzer erhält dadurch keinen Zugang
zum internen Backoffice oder zu fremden Tickets.

## 6. E-Mail-Kommunikation pro Projekt

Benachrichtigungen an Support-Mitarbeiter und Endnutzer werden von helpaffe
aus per E-Mail verschickt. Jedes Projekt benötigt dafür eine eigene,
unabhängig konfigurierbare Versand- und Darstellungskonfiguration:

- Eigene SMTP-Zugangsdaten und Versandparameter.
- Eigene Absenderadresse und eigenen Absendernamen passend zur Produktdomain.
- Eigenes Branding und eigene E-Mail-Templates.
- Anpassbare Betreffzeilen und Nachrichtentexte.

Diese Einstellungen müssen pro Projekt gepflegt werden können. Eine Änderung
für Produkt A darf den Versand oder den Auftritt von Produkt B nicht verändern.
Auch Links in Benachrichtigungen müssen zum jeweiligen Empfänger und Produkt
passen, etwa zur Ticketansicht im Produkt für einen Endnutzer.

Das Anlegen und Verwalten der Templates gehört ausdrücklich zum CLI-Umfang.

### Feste Benachrichtigungsereignisse

| Ereignis | Benachrichtigung |
| --- | --- |
| Neues Ticket | Eingangsbestätigung an den Kunden und Nachricht an die Support-Empfänger des Projekts. |
| Neue Kundenantwort | Nachricht an den Zuständigen; bei unzugewiesenen Tickets an die Support-Empfänger des Projekts. |
| Öffentliche Support-Antwort | Nachricht an den Kunden. |
| Neue Zuweisung | Nachricht an den neu zuständigen Support-Mitarbeiter, sofern er die Zuweisung nicht selbst vorgenommen hat. |
| Interne Notiz oder reine Status-/Prioritätsänderung | Keine E-Mail; der Vorgang bleibt im Verlauf sichtbar. |

Jedes Projekt besitzt eine einfache Liste von Support-E-Mail-Adressen.
Diese kann auch eine gemeinsame Team-Adresse enthalten. Individuelle
Benachrichtigungsregeln sind im MVP nicht vorgesehen.

### Templates und Sprache

Für jeden Benachrichtigungstyp wird eine fertige englische Vorlage mitgeliefert.
Betreff, Text und Gestaltung lassen sich pro Projekt überschreiben. Die
Vorlagen verwenden einen festen Satz von Variablen, etwa Produktname,
Kundenname, Ticketnummer, Betreff, Antworttext und Ticketlink. Ungültige
Variablen werden beim Speichern erkannt; frei programmierbare Template-Logik
ist im MVP nicht vorgesehen.

E-Mails werden als HTML mit zusätzlicher Textfassung verschickt. Vorschau und
Testversand sind über Weboberfläche und Administrator-CLI verfügbar.

Pro Projekt gibt es eine Spracheinstellung, deren einzige auswählbare Sprache
zunächst **Englisch** ist. Das MVP unterstützt keine Mehrsprachigkeit, weder
in der Systemoberfläche noch bei E-Mail-Templates. Individuelle Kundensprachen
sind ebenfalls nicht vorgesehen. Die deutsche Sprache dieses Visionsdokuments
ist davon unabhängig.

### Versand und Fehlerbehandlung

Eine Antwort wird zuerst im Ticket gespeichert. Der E-Mail-Versand erfolgt
anschließend im Hintergrund; ein SMTP-Ausfall darf die Antwort nicht verlieren
lassen. Der erste Versandversuch erfolgt sofort. Bei vorübergehenden Fehlern
folgen erneute Versuche nach 1, 5 und 30 Minuten. Danach bleibt der Versand als
fehlgeschlagen sichtbar und kann durch einen Menschen oder dessen berechtigten
Agenten erneut angestoßen werden.

Die Versandstatus sind **ausstehend**, **an SMTP übergeben** und
**fehlgeschlagen**. Die Übergabe an SMTP ist keine bestätigte Zustellung beim
Empfänger. Erneuter Versand erzeugt keine zweite Antwort im Ticket.

## 7. Agenten als vollwertiger Arbeitsweg

Die wichtigste Anforderung an helpaffe ist die umfassende Bedienbarkeit über
eine für KI-Agenten entwickelte CLI. Eine Funktion gilt nicht als vollständig,
wenn sie ausschließlich über die Weboberfläche erreichbar ist, sofern sie
nicht ausdrücklich zu den wenigen ausgenommenen Administrationstätigkeiten
gehört.

Zum CLI-Umfang gehören insbesondere:

- Tickets projektübergreifend oder innerhalb eines Projekts finden und lesen.
- Tickets bearbeiten, beantworten und ihren Bearbeitungsstand verwalten.
- Den für die Bearbeitung nötigen Verlauf und Kontext abrufen.
- Projekte und ihre alltägliche Support-Konfiguration verwalten, soweit die
  jeweilige Rolle dazu berechtigt ist.
- E-Mail-Templates anlegen, lesen, ändern und verwalten.

Die CLI soll klare Befehle, verlässliche Fehler- und Rückgabewerte sowie
maschinenlesbare Ausgaben bieten. Häufige Arbeitsabläufe sollen mit wenigen
Aufrufen und überschaubarem Kontext möglich sein. Agenten benötigen für diese
Arbeit keine Browser-Automatisierung.

Weboberfläche und CLI greifen auf dieselbe fachliche API und dieselben
Berechtigungsregeln zu. Aktionen müssen nachvollziehbar einem handelnden
Benutzer beziehungsweise dessen Agenten zugeordnet werden können.

## 8. Rollen und ihre Agenten

Es gibt zwei feste menschliche Rollen und die jeweils in ihrem Auftrag
handelnden KI-Agenten. Eine konfigurierbare Rechtematrix ist nicht vorgesehen.

| Rolle | Aufgabe und Zugriff |
| --- | --- |
| Support-Mitarbeiter | Bearbeitet alle Tickets in den für ihn freigegebenen Projekten, antwortet, ändert Zuständigkeiten und liest Support-Hinweise. |
| Agent eines Support-Mitarbeiters | Führt delegierte Support-Aufgaben innerhalb der dafür erteilten Rechte aus. |
| Administrator | Hat Zugriff auf alle Projekte, kann Projekte anlegen und konfigurieren, übernimmt die übergreifende Administration und kann sämtliche Support-Aufgaben ausführen. |
| Agent eines Administrators | Kann delegierte Konfigurations- und Support-Aufgaben ausführen, einschließlich der Template-Verwaltung; ausdrücklich ausgenommene Administrationstätigkeiten bleiben ausgeschlossen. |

Es gibt **eine gemeinsame CLI**. Ein Administrator braucht weder eine zweite
CLI-Installation noch ein separates Support-Konto, um selbst oder mit seinem
Agenten Tickets zu bearbeiten. Die verfügbaren Aktionen ergeben sich aus den
Berechtigungen, nicht aus unterschiedlichen Werkzeugen.

### Projektkonfiguration

SMTP-Einstellungen, E-Mail-Templates und Support-Hinweise werden durch
Administratoren oder deren Agenten verwaltet. Ein Administrator-Agent darf
auch Projekte anlegen. Produkt-API-Schlüssel gehören ebenfalls zur
Administration, unterliegen aber der unten festgelegten Ausnahme für Zugänge.

SMTP-Zugangsdaten können gesetzt oder ersetzt werden. Gespeicherte Passwörter
werden nicht wieder im Klartext ausgegeben.

### Agenten-Zugänge und Projektumfang

Jeder Agent erhält einen eigenen benannten Zugang, der einem menschlichen
Benutzer gehört. Gemeinsam verwendete Benutzer-Tokens sind dafür nicht
vorgesehen. Der Verlauf zeigt beispielsweise „Support-Agent von Alex“.

Ein Agent übernimmt die Rolle seines Benutzers, mit Ausnahme der ausschließlich
menschlichen Administration. Sein Zugang gilt wahlweise für alle für diesen
Benutzer erlaubten Projekte oder eine ausgewählte Teilmenge. Weitere
individuelle Einzelrechte sind zunächst nicht vorgesehen. Die Einstellung
„alle erlaubten Projekte“ schließt künftig hinzukommende berechtigte Projekte
automatisch ein; beim Administrator gilt das für alle neuen Projekte.

Jeder Agenten-Zugang ist einzeln widerrufbar. Wird der zugehörige Benutzer
deaktiviert oder verliert er Projektzugriff, gilt das unmittelbar auch für
seine Agenten.

Für ein Ticket bleibt der menschliche Benutzer zuständig. Zusätzlich hält der
Verlauf fest, welcher Agent tatsächlich gearbeitet hat. „Meine Tickets“
umfasst damit auch die Arbeit der eigenen Agenten.

### Ausschließlich menschliche Administration

Drei Bereiche bleiben Menschen vorbehalten und sind für Agenten gesperrt:

| Bereich | Berechtigung und Umfang |
| --- | --- |
| Benutzer und Rechte | Administratoren legen Benutzer an oder deaktivieren sie, ändern Rollen und vergeben Projektzugriffe. |
| Zugänge und Schlüssel | Support-Mitarbeiter dürfen eigene Agenten-Zugänge innerhalb ihrer bestehenden Rechte erstellen und widerrufen. Administratoren verwalten alle Agenten-Zugänge sowie die Produkt-API-Schlüssel. |
| Endgültiges Löschen | Das unwiederbringliche Löschen von Projekten oder Tickets bleibt der menschlichen Administration vorbehalten und gehört nicht zum normalen Support-Ablauf. |

Diese Aktionen erfolgen im angemeldeten Webkonto. Ein zusätzlicher
Freigabeprozess ist nicht vorgesehen. Alle anderen Funktionen im vereinbarten
Umfang sind für den jeweils berechtigten Agenten direkt ausführbar.

## 9. Ein festes, einfaches Ticketmodell

Alle Projekte verwenden denselben Workflow, dieselben Status und dieselben
Ticketfelder. Es gibt keine projektspezifischen Workflows, Pflichtkategorien,
frei definierbaren Felder, separaten Tickettypen oder SLA-Konfiguration zum
Start. Fehlerberichte und allgemeine Fragen nutzen dasselbe Modell.

### Status und Übergänge

| Status | Bedeutung |
| --- | --- |
| Offen | Eine Anfrage braucht Bearbeitung durch den Support. |
| In Bearbeitung | Ein Mensch oder Agent arbeitet gerade daran. |
| Wartet auf Kunde | Der Support braucht eine Antwort oder Information vom Endnutzer. |
| Gelöst | Aus Sicht des Supports ist die Anfrage erledigt. |

- Neue Tickets starten als **Offen**.
- Eine Kundenantwort auf **Wartet auf Kunde** oder **Gelöst** setzt das Ticket
  wieder auf **Offen**.
- Eine Kundenantwort während **In Bearbeitung** ändert den Status nicht,
  wird aber als neue Aktivität erkennbar.
- Beim Antworten kann der Support direkt den passenden Status setzen:
  weiterbearbeiten, auf Kundenantwort warten oder lösen.
- **Gelöst** ist jederzeit wieder öffnbar. Es gibt keinen zusätzlichen Status
  „Geschlossen“ und keine automatische Schließung nach einer Frist.
- Ist die Bearbeitung intern blockiert, bleibt das Ticket **In Bearbeitung**.
  Eine interne Notiz hält den Grund fest; ein weiterer Status ist dafür nicht
  vorgesehen.

### Ticketfelder

| Feld | Festlegung |
| --- | --- |
| Projekt | Genau eines; bestimmt Produktzugehörigkeit und Kommunikation. |
| Ticketnummer | Kurze, innerhalb der Instanz eindeutige Referenz. |
| Betreff | Kurze Zusammenfassung. |
| Anfragender | Produktbezogene Benutzerreferenz, Name und E-Mail-Adresse. |
| Unterhaltung | Ursprüngliche Anfrage, Antworten und interne Notizen. |
| Status | Einer der vier festen Status. |
| Priorität | Normal oder Dringend; standardmäßig Normal. |
| Zuständiger | Optional genau ein Support-Benutzer. |
| Zeitangaben | Erstellt, zuletzt geändert und letzte Kundenantwort. |

Ein Ticket darf zunächst unzugewiesen sein. Der Support-Benutzer oder sein
Agent übernimmt es bei Arbeitsbeginn. Der Agent handelt dabei im Auftrag
seines Benutzers; wer tatsächlich gehandelt hat, bleibt im Verlauf erkennbar.

### Unterhaltung

Ein gemeinsamer Ticketverlauf enthält klar unterscheidbare Einträge:

- **Öffentliche Antworten** sind für den Endnutzer sichtbar und lösen eine
  E-Mail-Benachrichtigung aus.
- **Interne Notizen** sind ausschließlich für Support und dessen Agenten
  sichtbar.
- **Systemereignisse** dokumentieren unter anderem Statuswechsel und
  Änderungen der Zuständigkeit.

Bereits versendete Antworten werden nicht nachträglich bearbeitet. Eine
Korrektur erfolgt als neue Antwort, damit E-Mail und Ticketverlauf zusammenpassen.
Das MVP unterstützt keine Anhänge und keine Bild- oder Datei-Uploads.
Diese Funktionen gehören zur späteren Roadmap; für das MVP ist damit auch
keine Infrastruktur zur Dateiablage oder Object Storage erforderlich.

### Produktkontext

Das angebundene Produkt kann beim Erstellen eines Tickets ein optionales
JSON-Objekt mit maximal 16 KB mitsenden, beispielsweise mit Produktversion,
betroffener Seite, Betriebssystem und Browser. Der Kontext wird lesbar
angezeigt und über die CLI ausgegeben. Er bleibt als Momentaufnahme der
Ticketerstellung erhalten und erhält keine eigenen Filter oder konfigurierbaren
Felder.

Das Produkt entscheidet, welche Angaben es überträgt. Passwörter,
Sitzungsschlüssel und andere Zugangsdaten gehören nicht in den Kontextblock.

## 10. Selbstständige Bearbeitung durch Agenten

Ein Agent darf innerhalb seiner Projektberechtigungen Tickets lesen, suchen
und übernehmen, Priorität und Zuständigkeit ändern, interne Notizen schreiben,
Antworten direkt an Kunden senden sowie Tickets lösen und wieder öffnen.
Ein Administrator-Agent darf zusätzlich Templates und alltägliche
Projekteinstellungen verwalten.

Es gibt keinen verpflichtenden Entwurfs- oder Freigabemodus. Ob ein Agent eine
Antwort vorher mit seinem Benutzer besprechen soll, ist eine Arbeitsanweisung
an diesen Agenten und kein vorgeschriebener Produktworkflow. Der Agent erhält
keine weitergehenden Rechte als der Benutzer, in dessen Auftrag er handelt;
die ausdrücklich ausgenommenen Administrationstätigkeiten bleiben ausgenommen.

### Der Arbeitsablauf über die CLI

1. **Nächstes Ticket holen und übernehmen:** innerhalb eines Projekts oder
   über alle berechtigten Projekte. Offene, verfügbare Tickets werden nach
   Priorität angeboten: dringende zuerst, danach die am längsten wartenden.
2. **Kontext lesen:** Ticketdaten, Unterhaltung, interne Notizen und relevante
   Produkthinweise sind in einem Aufruf abrufbar.
3. **Antworten und Status setzen:** Eine Antwort und der dazugehörige
   Statuswechsel sind in einem Arbeitsschritt möglich.
4. **Das nächste Ticket bearbeiten.**

Gezielte Suche und direkte Bearbeitung über die Ticketnummer bleiben möglich.

### Übernahme, Reihenfolge und parallele Bearbeitung

„Nächstes Ticket übernehmen“ wählt ein offenes Ticket, das unzugewiesen oder
dem eigenen Benutzer zugewiesen ist. Auswahl, Zuweisung und Statuswechsel auf
**In Bearbeitung** erfolgen in einem atomaren Schritt. Zwei parallele Aufrufe
dürfen nicht dasselbe Ticket übernehmen, auch wenn zwei Agenten für denselben
Benutzer arbeiten.

Dringende Tickets kommen vor normalen Tickets. Innerhalb derselben Priorität
wird das Ticket zuerst angeboten, das am längsten auf Support wartet. Die
Wartezeit beginnt beim Erstellen oder erneuten Öffnen. Weitere Kundenantworten
auf ein bereits offenes Ticket setzen sie nicht zurück.

Jedes Ticket erhält eine Versionsnummer. Schreibversuche auf Grundlage eines
veralteten Stands werden mit einem Konflikt abgewiesen und erfordern erneutes
Lesen. Das gilt insbesondere, wenn während der Bearbeitung eine neue Nachricht
eingeht. Eindeutige Anfrageschlüssel in der API verhindern, dass die Wiederholung
eines Aufrufs nach einem Verbindungsabbruch dieselbe Antwort doppelt anlegt.
Diese Regeln gelten über die gemeinsame API auch bei paralleler Arbeit in
Weboberfläche und CLI.

Im MVP gibt es keine automatische Freigabe nach Zeitablauf. Bricht ein Agent
ab, bleibt das Ticket **In Bearbeitung**. Ein berechtigter Benutzer oder Agent
kann es ausdrücklich wieder öffnen oder weiterbearbeiten.

### Support-Hinweise pro Projekt

Jedes Projekt erhält ein einfaches Markdown-Dokument „Support-Hinweise“ für
Produktbeschreibung, wichtige Links, bekannte Einschränkungen und Hinweise
zur Kommunikation. Administratoren und deren Agenten können es über Weboberfläche
beziehungsweise CLI pflegen. Support-Mitarbeiter und ihre Agenten können es
lesen und zusammen mit dem Ticketkontext abrufen.

Damit erhält ein Agent den nötigen Produktkontext, ohne dass dafür zunächst
ein umfangreiches Wissensdatenbanksystem nötig ist.

## 11. Eine gemeinsame Arbeitsliste

Die Standardansicht zeigt alle betreuten Projekte gemeinsam. Sie bietet die
Filter **Offen**, **In Bearbeitung**, **Wartet auf Kunde**, **Gelöst** und
**Meine Tickets**. Projekt, Priorität und Zuständiger sind zusätzliche Filter.
Konfigurierbare Boards oder eigene Ansichten pro Projekt sind zum Start nicht
vorgesehen.

## 12. Gemeinsame Grundlage mit planaffe und hostingaffe

helpaffe soll sich in Erscheinungsbild, Navigation und Interaktionsmustern wie
ein Produkt derselben Familie anfühlen. Bestehende Lösungen aus planaffe und
hostingaffe sind der Ausgangspunkt für die Weboberfläche und den technischen
Aufbau.

Die technische Orientierung umfasst deren .NET-Backend, React-Weboberfläche
und PostgreSQL-Datenhaltung sowie das Muster einer gemeinsamen API für
Weboberfläche und CLI. Self-Hosting soll mit einem überschaubaren Betrieb und
einem einfachen Container-Setup möglich sein.

Konkrete Versionen, wiederverwendbare Komponenten und die Implementierung der
CLI werden bei der technischen Ausarbeitung festgelegt. Für die Produktintegration
ist ein serverseitiges .NET-SDK als erstes SDK gesetzt. Fachliche Einschränkungen
der anderen Produkte werden nicht
automatisch übernommen: helpaffe benötigt insbesondere produktbezogene
Konfiguration und unterschiedliche Rechte für Support und Administration.

## 13. Grenzen des bisherigen Zielbilds

helpaffe übernimmt das Support-Backoffice. Die Endnutzer-Oberfläche ist Teil
des angebundenen Produkts; ein von helpaffe gehostetes Endnutzerportal ist
keine Voraussetzung dieser Vision.

E-Mail ist der festgelegte Benachrichtigungskanal. Antworten auf bestehende
Tickets per E-Mail sind für die spätere Roadmap vorgesehen, nicht für das MVP.
Das Anlegen neuer Tickets per E-Mail ist bisher nicht Bestandteil des Umfangs.
Ebenso sind ein autonomer
KI-Support innerhalb von helpaffe, weitere Kommunikationskanäle oder ein
allgemeines CRM bisher kein Bestandteil des beschriebenen Umfangs. Die
Agentenfähigkeit entsteht zunächst durch die CLI für die Agenten der Benutzer.

## 14. Woran wir den Erfolg erkennen

Das Zielbild ist erfüllt, wenn ein Team mindestens zwei eigenständige Produkte
über dieselbe helpaffe-Instanz betreuen kann und dabei:

- Endnutzer ihre Tickets in der Oberfläche ihres jeweiligen Produkts anlegen
  und verfolgen können.
- Support-Mitarbeiter den gesamten Support zentral bearbeiten können.
- Die Agenten der Support-Mitarbeiter den täglichen Ticketablauf über die CLI
  erledigen können.
- Ein Administrator mit derselben CLI sowohl Support leisten als auch seinen
  Agenten projektbezogene Einstellungen und Templates pflegen lassen kann.
- Benachrichtigungen für jedes Produkt mit dessen SMTP-Zugang, Absender,
  Branding und Texten verschickt werden.
- Produkt- und Benutzergrenzen trotz zentraler Bearbeitung eingehalten werden.

## 15. MVP-Umfang

Das MVP bildet den beschriebenen Kern aus den Abschnitten 1 bis 14 ab:
mehrere Produkte in einem zentralen Helpdesk, Produktintegration über API und
SDK, das feste Ticketmodell, Weboberfläche und umfassende Agenten-CLI,
Rollen und Projektberechtigungen, Support-Hinweise sowie
projektspezifische SMTP-Einstellungen und E-Mail-Templates. Der Hauptanwendungsfall
bleibt ein Administrator, der mit seinem Agenten den gesamten Support bearbeitet.

Das MVP ist ausschließlich englischsprachig. Pro Projekt ist Englisch als
einzige Sprache auswählbar. Anhänge und sämtliche Bild- oder Datei-Uploads
sind vom MVP ausgeschlossen und auf die spätere Roadmap verschoben.

Zusätzlich gehören folgende vier Ergänzungen zum MVP:

| Ergänzung | Umfang |
| --- | --- |
| Produktkontext beim Erstellen | Das angebundene Produkt kann technische Angaben wie Produktversion, betroffene Seite oder Betriebssystem als optionalen Kontextblock mitsenden. Dieser ist für den Support und seine Agenten abrufbar. Es entstehen dadurch keine frei konfigurierbaren Ticketfelder. |
| Bisherige Tickets desselben Kunden | Support und Agenten können die anderen Tickets desselben Endnutzers innerhalb desselben Projekts abrufen, um frühere Lösungsversuche und wiederkehrende Probleme zu erkennen. Eine Kundenkartei oder ein CRM ist dafür nicht vorgesehen. |
| Ähnliche Fälle finden | Die reguläre Volltextsuche mit Projekt- und Statusfilter ermöglicht das Auffinden früherer gelöster Fälle. Eine KI- oder Vektorsuche ist für diesen Umfang nicht erforderlich. |
| Fehlgeschlagene E-Mails sichtbar machen | Fehlgeschlagener Benachrichtigungsversand ist am Ticket erkennbar. Support und Agenten können den Versand über Weboberfläche beziehungsweise CLI erneut anstoßen. Der Versandstatus darf keine erfolgreiche Zustellung behaupten, die das System nicht feststellen kann. |

Die folgenden Roadmap-Punkte sind ausdrücklich keine Voraussetzung für den
Abschluss des MVP. Auch spätere Funktionen unterliegen dem Grundsatz der
CLI-Abdeckung und den bestehenden Rollen- und Projektberechtigungen.

## 16. Roadmap nach dem MVP

### Nächste Erweiterungen

Diese Funktionen haben nach dem MVP Vorrang. Ihre genaue Reihenfolge wird
bei der Umsetzungsplanung festgelegt.

| Erweiterung | Nutzen und Begrenzung |
| --- | --- |
| Wiedervorlage | Ein Ticket kann bis zu einem Zeitpunkt zurückgestellt werden, etwa bis ein Kunde testen konnte oder ein Fix veröffentlicht ist. Danach erscheint es wieder in der Arbeitsliste. Ein Datum genügt; es gibt dafür keinen zusätzlichen Status und kein konfigurierbares Regelwerk. |
| Verknüpfung mit Entwicklungsaufgaben | Ein Ticket kann Links zu Aufgaben in planaffe, GitHub oder GitLab enthalten. Zunächst werden nur Referenzen hinterlegt; eine automatische Synchronisation gehört nicht dazu. |
| Auf neue Arbeit warten | Die CLI kann warten, bis ein bearbeitbares Ticket oder eine Kundenantwort vorliegt. Ein bereits laufender Agent muss dadurch nicht ständig abfragen. Start und Betrieb des Agenten bleiben außerhalb von helpaffe. |

### Spätere Erweiterungen

Diese Punkte gehören zur geplanten Richtung, haben aber noch keinen festen
Release-Termin.

| Erweiterung | Nutzen und Begrenzung |
| --- | --- |
| Sammlung bewährter Lösungen | Kurze interne Markdown-Artikel pro Projekt ergänzen die allgemeinen Support-Hinweise. Support und dessen Agenten können Lösungen aus erledigten Fällen festhalten und wiederverwenden. Ein öffentliches Wissensportal ist zunächst nicht vorgesehen. |
| Anhänge und Datei-Uploads | Bilder und Dateien können später als Anhänge zu Ticketnachrichten unterstützt werden. Dateitypen, Größen- und Mengenlimits, Zugriffsrechte sowie Speicherung und eine mögliche Object-Storage-Anbindung werden für diese Roadmap-Stufe ausgearbeitet. Das MVP benötigt keine Upload- oder Dateispeicher-Infrastruktur. |
| Tickets zum selben Problem bündeln | Zusammengehörige Tickets lassen sich verknüpfen, ohne die Kundenunterhaltungen zu vermischen. Der Agent kann betroffene Kunden anschließend gesammelt über einen Fix informieren. Daraus entsteht keine neue Planungshierarchie. |
| Antworten direkt per E-Mail | Antworten auf Benachrichtigungen werden dem bestehenden Ticket zugeordnet und als Kundenantwort aufgenommen. Empfang, verlässliche Zuordnung und Umgang mit Anhängen werden vor der Umsetzung gesondert ausgearbeitet. |

## 17. Noch auszuarbeiten

Diese Vision legt die Richtung fest. Folgende Details bleiben bewusst offen:

- Konkreter API-Vertrag und Ausgestaltung des serverseitigen .NET-SDKs
  auf Grundlage des festgelegten Integrations- und Identitätsmodells.
- Vollständiger Variablenkatalog und technische Umsetzung der E-Mail-Templates
  sowie des Hintergrundversands auf Basis der festgelegten Versandregeln.
- Technische Umsetzung der atomaren Ticketübernahme, Versionsprüfung und
  eindeutigen Anfrageschlüssel auf Basis der festgelegten Bearbeitungsregeln.
- Technische Ausgestaltung der Roadmap-Funktionen, insbesondere Wiedervorlage,
  CLI-Wartefunktion, Anhänge einschließlich Speicherung und eingehende
  E-Mail-Antworten.
- Reihenfolge der Umsetzung innerhalb des MVP und der Roadmap-Stufen.
