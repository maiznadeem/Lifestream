# Changelog

All notable changes to Lifestream are listed here. The section of a version is also the text of its release on the GitHub Releases page.

## [2.1.2] - 2026-11-20

Lifestream 2.1.2 fixes defects found by using the app end to end against a real server.

### Fixed

- PLACEHOLDER

## [2.1.1] - 2026-11-20

Lifestream 2.1.1 fixes what 2.1.0 got wrong in a real window, and brings a new typeface.

### Look and feel

- A new typeface, Google Sans Flex (open source, SIL Open Font License), replaces Onest.
- Every text in a window is now drawn the same way, so headings and small print read as one typeface. In 2.1.0 the headings and the small text were drawn differently and could look like two fonts.
- Small text is evenly spaced: "2.1.0" and times such as "10:30" no longer close up.

### Fixed

- The title bar showed every button twice (minimize, maximize, close) on Windows 11: Windows drew its own buttons under ours. There is one set now.
- Dialogs no longer show a rectangular outline around the card.
- A maximized window no longer loses a few pixels at each screen edge, and the top rows of a page can be clicked again instead of dragging the window.
- The bell in the title bar opens a list of what needs attention, each with its action, instead of jumping to the dashboard.
- Opening Change password from the account menu no longer reloads the Profile page behind it and fails with "The server is taking too long to answer". The demo account, which has no password to change, is told so instead of being offered the dialog.
- A focus ring shows only when you move with the keyboard, never after a mouse click, and it follows the shape of the control.
- The collapsed sidebar keeps each page's count, as a badge on its icon.
- Appointments: times such as 09:00 are no longer cut off when the list opens, and its columns keep their widths.

## [2.1.0] - 2026-11-20

Lifestream 2.1 brings a new, calmer design built for the PC it runs on, and finishes every screen to the same pattern: the same paging, the same panel
beside the list, the same place for each action, and the details the work needs (which requests stock can cover, which bag to use first, where each
donation went).

### Look and feel

- A new light look, with a matching dark theme. Lifestream follows the Windows light or dark setting, or you can choose one from the account menu or the command palette.
- A new typeface, Onest, tuned to line up with the screen's pixels so small text stays sharp.
- On Windows 11 the window uses the Mica material, and its title bar holds the search field, notifications and your account.
- Red is now kept for blood groups, the logo and real problems, so a group that is out of stock stands out. The main action on each screen is a dark button.
- Scrollbars stay thin and out of the way until you move the pointer to them.
- New icons and a new Lifestream mark.

### Finding your way

- The sidebar is grouped into Donations, Transfusions, Inventory and Administration, shows how many appointments are still to come today, how many requests are open, and marks stock that is below its minimum. Press Ctrl+B to shrink it to icons; in a small window it does this by itself.
- Press Ctrl+K to search donors, patients and requests, go to any screen, or run an action such as Record donation, from the keyboard.
- Ctrl+D records a donation for today's next appointment, Alt+1 to Alt+8 open the sidebar screens, and F6 moves between the title bar, the sidebar and the page.

### Sign-in

- A new sign-in screen with the red-cell compatibility chart, a clear Sign in button and Try the demo below it.
- It tells you whether the server is ready before you sign in, and warns you when Caps Lock is on.
- It can remember your user name on this PC (never your password).
- While the server wakes up, the taskbar button shows progress, even when the window is minimized.

### Dashboard

- The dashboard now starts with what needs attention: requests that stock cannot cover, bags about to expire, groups below their minimum and requests due soon, each with the button that deals with it.
- Today's appointments show who has donated, who is next and who is giving blood for the first time.
- Stock by blood group shows each group's units, the share that expires within 7 days and the minimum.
- Donations and issues over the last 12 months are a line chart, with the same numbers available as a table.
- The taskbar button shows how many blood groups are below their minimum.

### Every list

- Every list has the same footer: how many records it is showing ("Showing 1–50 of 118") and the page controls.
- Selecting a row opens its details in a panel on the right, which reaches the edge of the window. The panel's actions are always in the same order: the
  main action as the one dark button, then Edit, then a "..." menu that holds the rare and the irreversible ones under their full names ("Delete staff
  member", "Cancel appointment"). Those ask first, and the question names the record.
- Each screen has one dark button at a time: while a panel is open, the button in the page header steps back.
- Names are shown in full. The name column takes the room its names need and the other columns share the rest; in a small window the least important
  column folds away instead of a name being cut, and the filters move under the search field instead of running off the edge.
- Records have one kind of code everywhere, on screen and on printed receipts: D-0175 for a donor, P-0053 for a patient, A-0905 for an appointment,
  R-0148 for a request, RC-0001 for a receipt, S-0002 for a staff member and B-0412 for a blood bag.
- Every status is a word with an icon or a dot, never a colour alone.
- When a list is empty or cannot be loaded, the message sits where the list would be, says why, and offers the next step.

### Donors

- Filter by eligibility and by when someone last donated, as well as by blood group.
- The list shows how many times each donor has donated. A donor who cannot donate is marked, and the panel says why.
- The panel shows the donation history: each donation, its bag, and where the bag went (issued to which patient and when, expired, or still in stock),
  with the donor's totals.

### Patients

- The list shows who is waiting for blood. The panel lists each waiting request with its urgency and whether stock can cover it, with a Fulfil button,
  and says which blood groups the patient can receive.

### Appointments

- Tabs for Today, Upcoming, Past and Cancelled, each with its count.
- Today is the day's agenda: who has donated, who is next, who is donating for the first time, and Record donation on each appointment still to come.
- Filter by donor with a chip that searches as you type.

### Requests

- Tabs for Open, Due in 48 h, Fulfilled and Cancelled, each with its count. Open requests are listed by the date the blood is needed, overdue first.
- A Cover column says whether stock can fulfil each request, how many units are short, or that there is no stock of that group.
- Before you fulfil a request, the panel lists the bags that will be issued, oldest first, each with its code, its donor and its use-by date.

### Stock

- Stock by age: for each blood group, how many bags are in each week of their shelf life, with the bags that expire within 7 days in their own column.
- The panel lists the group's bags in the order to use them, with each bag's code, collection date, expiry and days left, and shows what the group
  can be given to and can receive.

### Receipts

- Tabs for all receipts, donations and blood issues, each with its count. The date and time, the kind and who issued a receipt are always shown in full.
- The panel shows the receipt as it prints, with Print, Save as PDF and a link to its appointment or request. Reprints are marked as copies.

### Staff and profile

- Staff: filter by salary range, see each person's photo, job title, role and what the role may do. The panel offers Edit and Reset password; Delete
  staff member is in the "..." menu.
- Your profile is one centred page: your account, your details, security, appearance and the keyboard shortcuts. Save and Discard appear only when
  something has changed.
- Forms mark the optional fields instead of every required one, and every field for a secret has a button to show what you typed.

### Accessibility

- Text and controls meet the contrast rules on every surface in both themes. A scroll bar's thumb is quiet at rest and clearly visible while you point
  at it, drag it, or move through a list with the keyboard.
- Nothing is cut off: a value that does not fit gets a wider column, wraps, or shows its full text in a tooltip.

### Demo

- The demo clinic now has a day in progress. Today's appointments are in it: the ones that are over have donated, with their receipts and their
  bags in stock, and the rest are still to come, one of them a first-time donor. Yesterday and tomorrow are full days too, so the Today tab, the
  dashboard and the sidebar show a clinic day in any time zone.
- The demo clinic keeps its hours in UTC (09:00 to 16:30), so its appointments appear at those hours in your own time.

### Server

The desktop app and the server are released together. For anyone who uses the API directly, 2.1 only adds to it; nothing that existed has changed.

- New read-only requests: a donor's donations with their bags, the counts behind the Appointments and Requests tabs, the bags a fulfil would issue, the
  bags of one blood group in use-first order, and receipt counts by kind.
- New optional fields on existing answers: the age bands of each group's stock, the number of donations on a donor in the list, and a first-time mark
  on an appointment.
- New optional filters on existing lists: donors by eligibility and by last donation, and appointments and requests by tab, with the caller's time
  zone offset so that "today" is the caller's day.
- Fixed: when many wrong sign-in attempts arrived at the same moment, some were answered "the system is busy" and were not counted towards the lockout.
  Every wrong attempt is now counted and answered as a wrong sign-in or as too many attempts.

### Known limits

- There is no "check in" step for an appointment: the server does not record one, so the agenda offers Record donation.
- Fulfilling a request issues the oldest bags of the group; the bags cannot be chosen by hand.
- The columns of a list cannot be chosen, and there is one row height.

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
