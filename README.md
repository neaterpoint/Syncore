# Syncore

Syncore is a shared project workspace for collecting team links and coordinating
tasks. Projects are private to their members and use email one-time-code sign-in.

## Features

- Email OTP authentication with Supabase Auth
- Project creation and invite-code joining
- Project member profiles and nicknames
- Shared resource links and task tracking
- Responsive Blazor UI styled with Tailwind CSS

Real-time updates and billing are not included in this release.

## Tech stack

- .NET 10 and Blazor Interactive Server
- Supabase Auth, PostgreSQL, REST API, and Row Level Security
- Tailwind CSS 4

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) and npm
- A Supabase project

## Local development

Clone the repository and install dependencies:

```powershell
git clone https://github.com/neaterpoint/Syncore.git
cd Syncore
dotnet restore
npm install
```

Build the stylesheet and start the HTTP development profile:

```powershell
npm run css:build
dotnet run --launch-profile http
```

The app is available at `http://localhost:5083`. To rebuild Tailwind CSS as
files change, run `npm run css:watch` in another terminal.

## Supabase setup

1. In the Supabase SQL Editor, apply
   `supabase/migrations/20261126000100_initial_schema.sql`. Skip this if it has
   already been applied.
2. Apply `supabase/migrations/20261126000200_backfill_existing_profiles.sql`.
   It can be run again safely.
3. In **Authentication → Sign In / Providers → Email**, enable email OTP and
   set the code length to six digits.
4. In **Authentication → URL Configuration**, set the local Site URL to
   `http://localhost:5083` and allow `http://localhost:5083/**`.
5. In **Authentication → Emails → Magic link or OTP**, make sure the template
   displays `{{ .Token }}` as the code. Remove link-only placeholders if the
   app should prompt for a code.
6. Configure a custom SMTP provider in **Authentication → SMTP Settings**.
   Supabase's default sender is restricted and rate-limited; it is intended
   only for testing.
7. Store the project URL and publishable key as local .NET User Secrets:

   ```powershell
   dotnet user-secrets set "Supabase:Url" "https://YOUR_PROJECT_REF.supabase.co"
   dotnet user-secrets set "Supabase:PublishableKey" "YOUR_PUBLISHABLE_KEY"
   ```

Never use a Supabase `service_role` key in this app. Keep credentials out of
source files, browser code, and chat. If an OTP request fails, check the
Supabase Auth logs and the app's server log for the provider error.

## Authentication and sessions

The browser sends OTP requests to same-origin application endpoints. The server
communicates with Supabase Auth and keeps access and refresh tokens in memory;
the browser receives only an opaque HttpOnly cookie. Supabase REST requests use
the signed-in user's JWT, so database Row Level Security policies remain in
force.

The in-memory session store is for local development and a single app instance.
Restarting the app signs users out. Use a shared protected session store before
deploying multiple app instances.

## Database source of truth

The SQL migrations in `supabase/migrations/` define the current database schema,
functions, and Row Level Security policies.

## Project structure

```text
Components/       Blazor layouts, pages, and shared components
Endpoints/        Same-origin authentication endpoints
Services/         Supabase Auth, session, and workspace services
Styles/           Tailwind CSS source
wwwroot/          Generated CSS, browser scripts, and image assets
supabase/         Database migrations
Properties/       .NET launch profiles
Program.cs        Application and middleware setup
```

## Roadmap

- [x] Email OTP authentication
- [x] Project creation and invite-code joining
- [x] Project links and task tracking
- [x] Responsive interface
- [ ] Real-time updates
- [ ] Billing and payments
- [ ] Automated tests and deployment
