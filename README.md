· This script is made for unity
· This script uses Bursted Jobs, so you need Burst and Jobs backages installed
· The script only removes the triangles, when not visible due being reversed, or in the back of the viewer, so it removes triangles from the back of the objects
imagine looking a sphere what only is sentt o the GPU is the triangles of just half sphere using this script
· This script doesnt removes the verteices (yet)
· This script still having space for optimization (in comments) jobs could be dependent so it may schedule them better, or they could become a single job that only executes the visibility algorithm once (it is executing twice, once per job)
· There is 2 Jobs, one processes the visible triangle count (not accounts for face occlusion, just uses triangle normals and positions) and return that number
· Then that "Visible triangle count" is used to generate a mesh with that exact triangle count, and then the other job process the triangles to render using the same "Visibility algorith" that the first job

