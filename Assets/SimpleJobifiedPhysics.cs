using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

public class SimpleJobifiedPhysics : MonoBehaviour
{
    // When passing a data collection into a Unity Job it has to be one of the new Unity Native types.
    // .net genrics (like List<T>) and plain arrays (like T[]) wil not work in jobs! 
    private NativeArray<Vector3> velocities;
    private NativeArray<Vector3> positions;
    private NativeArray<Matrix4x4> renderMatrices;
    private NativeArray<int> sleeping;
    private Vector3 gravity;
    private int objectCount = 1023; // the most we can fit into a single call to Graphics.DrawInstance

    public Mesh mesh;
    public Material material;

    public Transform spawnDirection;

    // This job applies gravity to each objects vecocity in the simulation
    struct GravityJob : IJobParallelFor
    {
        public float DeltaTime;

        public Vector3 Gravity;
        public NativeArray<Vector3> Velocities;
        public NativeArray<int> Sleeping;

        public void Execute(int i)
        {
            // has the object been marked as sleeping?
            // sleeping objects are object that have settled, so they dont
            // move at all in this case we skip adding gravity to make them
            // appear more stable
            if (Sleeping[i] > 0)
            {
                Velocities[i] = new Vector3(0, 0, 0);
                return;
            }

            // simply integrate gravity based on time since last step.
            Velocities[i] += Gravity * DeltaTime;
        }
    }

    // this job creates a batch of RaycastCommands we are going to use for
    // collision detection against the world. these can be sent to PhysX
    // as a batch that will be executed in a job, rather than us having to
    // call Physics.Raycast in a loop just on the main thread!
    struct PrepareRaycastCommands : IJobParallelFor
    {
        public float DeltaTime;

        public NativeArray<RaycastCommand> Raycasts;
        [ReadOnly]
        public NativeArray<Vector3> Velocities;
        [ReadOnly]
        public NativeArray<Vector3> Positions;


        public void Execute(int i)
        {
            // figure out how far the object we are testing collision for
            // wants to move in total. Our collision raycast only needs to be
            // that far.
            float distance = (Velocities[i] * DeltaTime).magnitude;
            Raycasts[i] = new RaycastCommand(Positions[i], Velocities[i], distance);
        }
    }

    // Integrate the velocity into all the objects positions. We use the
    // Raycast hit data to decide how much of the velocity to integrate -
    // we dont want to tunnel though the colliders in the scene!
    struct IntegratePhysics : IJobParallelFor
    {
        public float DeltaTime;
        [ReadOnly]
        public NativeArray<Vector3> Velocities;
        [ReadOnly]
        public NativeArray<RaycastHit> Hits;
        public NativeArray<int> Sleeping;
        public NativeArray<Vector3> Positions;

        public void Execute(int i)
        {
            if (Sleeping[i] > 0) // if the object is sleeping, we dont have to integrate anything
            {
                Sleeping[i]++;
                return;
            }

            if (Hits[i].normal == Vector3.zero)
            {
                // if there has been no colision, intergrate all of the velocity we want for this frame
                Positions[i] += Velocities[i] * (Velocities[i] * DeltaTime).magnitude;
            }
            else
            {
                // there has been a collision! just move up to the point of the collion with a tiny offset for stability
                Positions[i] = Hits[i].point + new Vector3(0, 0.1f, 0);
            }
        }
    }

    // Respond to collisions
    struct CalculateCollisionResponse : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<RaycastHit> Hits;

        public NativeArray<Vector3> Velocities;
        public NativeArray<int> Sleeping;

        public void Execute(int i)
        {
            // If there has been a collision, the RaycastHit normal will be
            // non zero. When we know there has been a collisoin we can
            // respond to it.
            if (Hits[i].normal != Vector3.zero)
            {
                // first, lets check if the velocity has got very low before this collision. if so
                // we are going to put the object to sleep. this will stop it from
                // reciveing gravity and will make the simulation appear more stable
                if (Velocities[i].magnitude <= 2f)
                {
                    Velocities[i] = Vector3.zero;
                    Sleeping[i]++; // increment the sleeping counter. any value over 1 is sleeping. We can use this to respawn objects after they have been static for while
                }
                // lets implement a very simple bounce effect - just be reflecting the velocity by the
                // collison normal (the normal is a vector pointing perpendicular away from the surface
                // that we have hit).
                // were also going to apply a damping effect to the velocity by removing force from the resulting
                // reflected velocity. You can think of this as a really simple simulation of force lost
                // due to friction against the surface we have collided with.
                else
                {
                    Velocities[i] = Vector3.Reflect(Velocities[i], Hits[i].normal);

                    var angleBetweenNormalAndUp = Vector3.Angle(Hits[i].normal, Vector3.up); // returns a value between 0-180
                    var lerp = angleBetweenNormalAndUp / 180f;
                    Velocities[i] = Velocities[i] * Mathf.Lerp(0.5f, 1f, lerp);
                }
            }
        }
    }

    // Calculate and store a matrix we can use to draw each object. As we dont support rotation in this demo, we pretty
    // much only supply the position. A fixed scale is also applied to make sure the meshes we draw arnt giant.
    struct CalculateDrawMatricies : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<Vector3> positions;
        public NativeArray<Matrix4x4> renderMatrices;

        public void Execute(int i)
        {
            renderMatrices[i] = Matrix4x4.TRS(positions[i], Quaternion.identity, new Vector3(0.5f, 0.5f, 0.5f));
        }
    }

    CustomSampler sampler;
    IEnumerator Start()
    {
        // lets wait for half a second before we set off the system -- just to avoid any stutters
        yield return new WaitForSeconds(0.5f);

        // create a sampler so we can mesure performance
        sampler = CustomSampler.Create("SimplePhysics");
        
        // lets define an rough approximation for gravity
        gravity = new Vector3(0, -9f, 0);

        // and set up all our NativeArrays with initial values
        velocities = new NativeArray<Vector3>(objectCount, Allocator.Persistent);
        positions = new NativeArray<Vector3>(objectCount, Allocator.Persistent);
        sleeping = new NativeArray<int>(objectCount, Allocator.Persistent);
        renderMatrices = new NativeArray<Matrix4x4>(objectCount, Allocator.Persistent);

        for (int i = 0; i < objectCount; i++)
        {
            Respawn(i);
        }
    }

    private void Respawn(int i)
    {
    //velocities[i] = Random.onUnitSphere * Random.Range(2, 10); // spawn objects with velocities spreading them out in a sphere
        velocities[i] = (Random.onUnitSphere + (((spawnDirection.position - transform.position).normalized).normalized)*1.3f).normalized * Random.Range(2f, 10f); // spawn with velocities arching towards a target object
        positions[i] = transform.position;
        sleeping[i] = 0;
        renderMatrices[i] = Matrix4x4.identity;
    }

    void Update()
    {
        // only do an update if the data has been initialized
        if (!positions.IsCreated)
            return;

        sampler.Begin();
        
        var deltaTime = Time.deltaTime;

        // FORCE ACCUMILATION

        // First off lets apply gravity to all the object in our scene that arnt currently asleep
        var gravityJob = new GravityJob()
        {
            DeltaTime = deltaTime,
            Gravity = gravity,
            Velocities = velocities,
            Sleeping = sleeping
        };
        var gravityDependency = gravityJob.Schedule(objectCount, 32);
        
        // TODO accumilate other forces here! gravity is just the simplest force to apply to our objects,
        // but theres no limit on the kind of forces we can simulate. We could add friction, air resistnace,
        // constant acceleration, joints and constrainsts, boyancy.. anything we can think of that can effect
        // the velocity of an object can have its own job scheduled here.

        // INTERGRATION AND COLLISION

        // Create temporary arrays for the raycast info. Lets use the TempJob allocator,
        // this means we have to dispose them when the job has finished - its short lived data
        var raycastCommands = new NativeArray<RaycastCommand>(objectCount, Allocator.TempJob);
        var raycastHits = new NativeArray<RaycastHit>(objectCount, Allocator.TempJob);

        // Lets schedule jobs to do a collision raycast for each object. One job Prepare    s all the raycast commands,
        // the second actually does the raycasts.
        var setupRaycastsJob = new PrepareRaycastCommands()
        {
            DeltaTime = deltaTime,
            Positions = positions,
            Raycasts =  raycastCommands,
            Velocities = velocities
        };

        var setupDependency = setupRaycastsJob.Schedule(objectCount, 32, gravityDependency );
        
        var raycastDependency  = RaycastCommand.ScheduleBatch(raycastCommands, raycastHits, 32, setupDependency );

        // Now we know if there is a collision along our velocity vector, its time to integrate the velocity into
        // our objects for the current timeset.
        var integrateJob = new IntegratePhysics()
        {
            DeltaTime = deltaTime,
            Positions = positions,
            Velocities = velocities,
            Sleeping = sleeping,
            Hits = raycastHits
        };
        var integrateDependency  = integrateJob.Schedule(objectCount, 32, raycastDependency );

        // finally, respond to any collisions that happened in the lsat update step.
        var collisionResponeJob = new CalculateCollisionResponse()
        {
            Hits = raycastHits,
            Velocities = velocities,
            Sleeping = sleeping
        };
        var collisionDependency  = collisionResponeJob.Schedule(objectCount, 32, integrateDependency );

        // Now the physics is done, we need to create a drawing matrix for every object. This simple demo dosnt
        // implment roation, so only the translation values in the matrix reallllly matter.
        var renderMatrixJob = new CalculateDrawMatricies()
        {
            positions = positions,
            renderMatrices = renderMatrices
        };
        var matrixJob = renderMatrixJob.Schedule(objectCount, 32, collisionDependency );

        // All the jobs we want to execute have been scheduled! By calling .Complete() on the last job in the
        // chain, Unity makes the main thread help out with scheduled jobs untill they are all complete. 
        // then we can move on and use the data caluclated in the jobs safely, without worry about data being changed
        // by other threads as we try to use it - we *know* all the work is done
        matrixJob.Complete();

        // make sure we dispose of the temporary NativeArrays we used for raycasting
        raycastCommands.Dispose();
        raycastHits.Dispose();

        // Well, all the updating is done! lets actually issue a draw!
        Graphics.DrawMeshInstanced(mesh, 0, material, renderMatrices.ToArray());
        
        // DEBUG 1 - draw red lines showing object velocity
        //for (int i = 0; i < objectCount; i++)
        //{
        ///   Debug.DrawLine(positions[i], positions[i] + velocities[i], Color.red, 0.016f, true);
        //}

        // DEBUG 2 - draw a trail for a few objects, really helps to visualize the bounce!
        //var duration = 01f;
        //Debug.DrawLine(positions[0], positions[0] + velocities[0], Color.red, duration, true);
        //Debug.DrawLine(positions[200], positions[200] + velocities[200], Color.cyan, duration, true);
        //Debug.DrawLine(positions[400], positions[400] + velocities[400], Color.green, duration, true);
        //Debug.DrawLine(positions[600], positions[600] + velocities[600], Color.magenta, duration, true);
        //Debug.DrawLine(positions[800], positions[800] + velocities[800], Color.yellow, duration, true);
        //Debug.DrawLine(positions[1000], positions[1000] + velocities[1000], Color.blue, duration, true);
        //Debug.DrawLine(positions[100], positions[100] + velocities[100], Color.red, duration, true);
        //Debug.DrawLine(positions[300], positions[300] + velocities[300], Color.cyan, duration, true);
        //Debug.DrawLine(positions[500], positions[500] + velocities[500], Color.green, duration, true);
        //Debug.DrawLine(positions[700], positions[700] + velocities[700], Color.magenta, duration, true);
        //Debug.DrawLine(positions[900], positions[900] + velocities[900], Color.yellow, duration, true);
    
        // finally lets respawn any object that has been asleep and stable for a while
        // TODO as of Unity 2018.1b6, NativeArray is the only native collection type. When
        // NativeQueue is included in the beta this last task can be jobified!
        for (int i = 0; i < objectCount; i++)
        {
            if (sleeping[i] > 15)
                Respawn(i);
        }
        
        sampler.End();
    }

    private void OnDisable()
    {
        velocities.Dispose();
        positions.Dispose();
        renderMatrices.Dispose();
        sleeping.Dispose();
    }
}
