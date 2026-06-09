import { Alert, Button, Form as BootstrapForm } from "react-bootstrap";
import styles from "./route.module.css"
import type { Route } from "./+types/route";
import { isAuthenticated, login } from "~/auth/authentication.server";
import { Form, redirect, useNavigation } from "react-router";
import { backendClient } from "~/clients/backend-client.server";

type LoginPageData = {
    loginError: string
}

export async function loader({ request }: Route.LoaderArgs) {
    // if already logged in, redirect to landing page
    if (await isAuthenticated(request)) return redirect("/");

    // if we need to go through onboarding, redirect to onboarding page
    const isOnboarding = await backendClient.isOnboarding();
    if (isOnboarding) return redirect("/onboarding");

    // otherwise, proceed to login page!
    return { loginError: null };
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
    const navigation = useNavigation();
    const isLoading = navigation.state == "submitting";
    const pageData = actionData || loaderData;
    const showError = !!pageData.loginError;
    const submitButtonDisabled = isLoading;
    const submitButtonText = isLoading ? "Logging in..." : "Login";

    return (
        <div className={styles["page"]}>
            <Form className={styles["container"]} method="POST">
                <div className={styles["brand"]}>
                    <img className={styles["logo"]} src="/logo.png?v=6" alt="davex" />
                    <div className={styles["title"]}>davex</div>
                </div>
                <div className={styles["subtitle"]}>Sign in to continue</div>
                <Alert className={styles["error"]} show={showError} variant="danger">{pageData.loginError}</Alert>
                <div className={styles["fields"]}>
                    <BootstrapForm.Control name="username" type="text" placeholder="Username" autoFocus />
                    <BootstrapForm.Control name="password" type="password" placeholder="Password" />
                </div>
                <Button type="submit" variant="primary" disabled={submitButtonDisabled}>{submitButtonText}</Button>
                <div className={styles["footer"]}>Secure local session</div>
            </Form>
        </div>
    );
}

export async function action({ request }: Route.ActionArgs) {
    try {
        var responseInit = await login(request);
        return redirect("/", responseInit);
    }
    catch (error) {
        if (error instanceof Error) {
            return { loginError: error.message };
        }
        throw error;
    }
}