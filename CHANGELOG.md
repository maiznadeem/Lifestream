# Changelog

All notable changes to Lifestream are listed here. The section of a version is also the text of its release on the GitHub Releases page.

## [2.0.2] - 2026-11-19

Lifestream 2.0.2 makes the new look sharper.

### Look and feel

- Borders and rounded corners are crisp at every display scale (100 % to 175 %): edges no longer look soft or doubled.
- Text is sharper, especially at small sizes: the app now ships a version of its font that is tuned to line up with the screen's pixels, and large headings use a display cut of the same font.
- Buttons and fields have slightly rounder corners, and pressed buttons no longer shift by a fraction of a pixel.

## [2.0.1] - 2026-11-19

Lifestream 2.0.1 is a polish release: the same features, with a new identity and a more refined look.

### Look and feel

- Lifestream now has its own icon, shown in the Start menu, the taskbar, the installer and the window title bar.
- The sign-in screen is redesigned, with a branded panel, the new logo and a more prominent "Try the demo" button.
- Buttons, fields, cards and dialogs have softer rounded corners.
- Buttons and other controls show clear hover, pressed and focus states, with short animations.
- Fields show a hint before you type and an accent line while they are focused.
- Cards and dialogs have soft shadows.
- Small text is sharper: it is now drawn aligned to the screen's pixels.

## [2.0.0] - 2026-11-19

Lifestream 2.0 is a ground-up rewrite of the original course project. Everything the old app did is still there; how it works, how fast it is and how it looks are new.

### Security

- The desktop app no longer talks to the database. It talks to a web service over HTTPS, and only that service reaches the database, with a managed identity instead of a password. There is no database address, user name or password inside the installer.
- Signing in uses short-lived access tokens that renew while you work and are revoked when you sign out. Passwords are stored only as salted hashes, are never sent back to the app, and are typed into masked fields everywhere. Administrators reset a password instead of reading one.
- What you may do is decided on the server for every action, not only by hiding buttons: staff and administrators have different rights, and a receipt always records the person who is actually signed in.
- The old shared database password and every other credential from the first version were retired; the database now accepts only Microsoft Entra sign-in.

### Speed

- The window never freezes. Every screen shows a placeholder layout immediately and fills in with real data after one request to the server.
- Lists load a page at a time and are searched, filtered and sorted on the server, so they stay quick with thousands of records. The dashboard loads with one request.
- The first request after a quiet period can take up to a minute while the server wakes up. The app tells you when that is happening and lets you cancel.

### Every screen

- New look: a dark theme with one consistent set of colors, type and spacing, clear focus outlines, and buttons that show when they are idle, hovered, pressed, busy or unavailable.
- Dashboard: donations and issues by month for the last 12 months (or another period), with a switch for each blood type, a "View as table" alternative, and cards for donors, patients, pending appointments and requests, and stock. Low and expiring stock is called out.
- Donors, patients, appointments, requests, receipts and staff: searchable, paged lists with a detail pane next to the list. Appointments, requests, receipts and staff show the name with the id (or user name) underneath, appointments and requests show their status as a chip, and the names in appointments, requests and receipts are links that open the donor, patient or record the row refers to. Donors and patients show the name on one line. Text that does not fit a cell is cut off with an ellipsis and its full text appears in a tooltip.
- Stock: one tile for each blood type with the units available, the units expiring soon and a low-stock flag, and two filters to choose which tiles to show. It is not a list.
- Choosing a donor or patient uses a search box instead of typing an id. A filter that has nothing chosen shows "Any donor" and a Choose action.
- Appointments: book from the free half-hour slots of a day, reschedule, cancel, and record the donation when the donor arrives. A donor who gave blood too recently is flagged and needs a reason to override.
- Requests and stock: fulfil a request from the bags in stock; stock shows what is fresh, expiring or low for each blood type.
- Receipts open as a printable page and can be saved as PDF.
- Staff: administrators manage accounts, roles, shifts and photos. Everyone has a profile page with a photo and a change-password form.
- Forms validate as you type, say what is wrong next to the field, and warn before you discard unsaved changes.
- Updates arrive automatically: the app checks for a newer version when it starts and installs it with a restart.

### Demo

- **Try the demo** on the sign-in screen opens the app without an account. It uses made-up people, a year of made-up history and a made-up clinic, is reset every day, and nothing in it is real.

### Known limits

- The installer is not code-signed, so Windows SmartScreen asks for confirmation (More info, then Run anyway). Compare the SHA-256 checksum of the download with the one on the release page first.
- The demo server sleeps when nobody uses it, which is why the first request can be slow.
- Windows 10 and 11 on 64-bit PCs only.
