/** The logged-in user as shared by HandleInertiaRequests (never the password hash). */
export type User = {
    id: string;
    username: string;
    full_name: string;
};

export type Auth = {
    user: User | null;
    /** Merged role permissions at HQ ("*" = all). */
    permissions: string[];
};
