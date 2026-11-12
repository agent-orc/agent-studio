import { Injectable, inject } from '@angular/core';
import { AuthSessionState } from './auth.service';

/**
 * AGT-3670: where the signed-in person reviews through the Reviewer role and
 * holds no card write. There a Human Review card offers the review decision
 * (accept, or send back with a note) instead of lane and run controls, which
 * the server would refuse. The routes still decide every request.
 */
@Injectable({ providedIn: 'root' })
export class ReviewerSession {
  private readonly session = inject(AuthSessionState);

  /** Signal-tracked: reads the session status inside a computed or template. */
  reviewsOnly(projectName: string | null | undefined): boolean {
    const projects = this.session.status()?.user?.reviewerProjects ?? [];
    if (projects.length === 0 || !projectName) return false;
    const wanted = projectName.toLowerCase();
    return projects.some((project) => project === '*' || project.toLowerCase() === wanted);
  }
}
