import { Form, Head } from '@inertiajs/react';
import InputError from '@/components/input-error';
import PasswordInput from '@/components/password-input';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Spinner } from '@/components/ui/spinner';

/** Login with username + password (same accounts as the stores). No registration, no password reset by email. */
export default function Login() {
    return (
        <>
            <Head title="Masuk" />

            <Form
                action="/login"
                method="post"
                resetOnSuccess={['password']}
                resetOnError={['password']}
                className="flex flex-col gap-6"
            >
                {({ processing, errors }) => (
                    <div className="grid gap-6">
                        <div className="grid gap-2">
                            <Label htmlFor="username">Username</Label>
                            <Input
                                id="username"
                                type="text"
                                name="username"
                                required
                                autoFocus
                                tabIndex={1}
                                autoComplete="username"
                                placeholder="Username"
                            />
                            <InputError message={errors.username} />
                        </div>

                        <div className="grid gap-2">
                            <Label htmlFor="password">Password</Label>
                            <PasswordInput
                                id="password"
                                name="password"
                                required
                                tabIndex={2}
                                autoComplete="current-password"
                                placeholder="Password"
                            />
                            <InputError message={errors.password} />
                        </div>

                        <Button
                            type="submit"
                            className="mt-2 w-full"
                            tabIndex={3}
                            disabled={processing}
                            data-test="login-button"
                        >
                            {processing && <Spinner />}
                            Masuk
                        </Button>
                    </div>
                )}
            </Form>
        </>
    );
}

Login.layout = {
    title: 'POS — Web Pusat',
    description: 'Masuk dengan username dan password Anda',
};
