window.syncoreAuth = {
    async sendOtp(email) {
        return this.post("/api/auth/otp", { email });
    },
    async verifyOtp(email, token) {
        return this.post("/api/auth/verify", { email, token });
    },
    async signOut() {
        return this.post("/api/auth/logout", {});
    },
    async post(url, body) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });

        if (response.ok) {
            return;
        }

        let message = "The request could not be completed. Please try again.";
        try {
            const problem = await response.json();
            message = problem.detail || problem.title || message;
        } catch {
            // Keep the generic message when the server does not return Problem Details.
        }

        throw new Error(message);
    }
};
