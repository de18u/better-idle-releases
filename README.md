# Better Idle

A Windows tray app that keeps Discord showing you as Idle and Steam showing you
as Away until you do something other people can see, such as sending a message
or joining voice. That switches you back right away, and you drop out again
once the client's own idle threshold passes without another visible action.

This repo holds `better-idle.cs`, the app's whole source, at each version tag.
The app reads the tags here to update itself. A minute after it starts, and
every six hours after that, it looks for a tag newer than its own version. If
there is one, it downloads that tag's `better-idle.cs`, builds it, puts the new
`better-idle.exe` where the old one was, and restarts.

## Run

Download `better-idle.exe` from the
[latest release](https://github.com/de18u/better-idle-releases/releases/latest)
and run it. A crescent moon appears in the tray. Right-click it to turn the
Discord and Steam hooks on or off, then restart whichever clients were already
running.

## Build

To build it yourself instead, run:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:better-idle.exe /r:System.Web.Extensions.dll better-idle.cs
```

That compiler ships with Windows, so there is nothing to install.

Modifying either client is against its terms: Discord's Terms of Service and
Valve's Subscriber Agreement.
