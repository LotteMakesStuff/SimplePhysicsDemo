# SimplePhysics Demo    <a href='https://ko-fi.com/A08215TT' target='_blank'><img height='46' style='border:0px;height:46px;' src='https://az743702.vo.msecnd.net/cdn/kofi3.png?v=0' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a> <a href='https://www.patreon.com/bePatron?u=7061709' target='_blank'><img height='46' style='border:0px;height:46px;' src='https://c5.patreon.com/external/logo/become_a_patron_button@2x.png' border='0' alt='Become a Patron!' /></a>

## Whats this demo good for?
SimplePhysics is a Unity 2018.1 demo showcasing a custom physics system which does all of its processing via the new Unity Jobs system.
![demo gif - boxes flying everywhere!](/Docs/phys.gif)
* Its a simplified, bare-bones version of the same concepts behind [LotteMakesStuff's HyperPhysics engine](https://twitter.com/LotteMakesStuff/status/964612077708070912).
* It uses the new Unity 2018.1 Job system to run the physics simulation across all your CPUs cores at once.
  * Its a good nontrivial example of how to chain job dependencies together and how to use NativeArrays
  * It demos Unity's RaycastCommand - a new way of sending batches of raycasts to be processed in jobs
* It's tiny!! (less than 300 lines!)
* It's open source- do what you want with it MIT licensed

## How do I run it?
Once you have cloned the repository, simply open Scenes/SampleScene.unity. There's a fairly basic little set up here, a floor, one wall to bounce off and of course, our physics system (its attached to the Physics Spawner game object). The floor and wall just has regular Unity colliders attached, nothing special there. The spawner only has 3 public properties, Mesh, Material and Spawn direction.
* Mesh and Material set the assets you want to render the physics objects with. Defaults to a pinkish cube.
* Spawn Direction defaults to the walls transform. The initial velocity for physics object will be angled towards this transform, spraying out in a satisfying arch.

## Wheres the code?
Its all contained in [`SimpleJobifiedPhysics.cs`](/Assets/SimpleJobifiedPhysics.cs).

## How does it work?
Unlike unity game objects and rigidbodies, our physics objects aren't represented by concrete objects in this engine, but by a "structure of arrays'. At the top of SimpleJobifiedPhysics we define a whole bunch of NativeArrays that hold all the various properties that make up a physics object. If you imagine these laid out in a table or excel sheet, each row across all the arrays is an individual object. 

Position | Velocity | Sleeping | RenderingMatrix
------------ | -------------|------------ | -------------
Vector3 | Vector3 | int | Matrix4x4
The position of the obect after the previous update | The direction and speed the object is moving in | <ul><li>Counts how long the object has been asleep for.</li><li>Sleeping == 0, the object is moving.</li><li>Sleeping >= 0 the objet is stable an at rest on the ground. Counter gets incremented every frame the object sleeps for.</li></ul>  | We need this to draw the object with Graphics.DrawMeshInstanced(...)

Okay, there are 5 steps in running this physics engine! All of these steps are implemented as an [IJobParallelFor](https://docs.unity3d.com/2018.1/Documentation/ScriptReference/Unity.Jobs.IJobParallelFor.html) job. These jobs split the work up into batches and run it on all free CPU cores in the system - allowing us to process a lot of stuff very fast.

1. Force Accumulation - Forces are things that affect the Velocity of our physics objects. Our first task is to calculate all these forces and merge them into Velocity. This demo only ships with one force generator - GravityJob. As long as an object isn't sleeping, we apply gravity to it. Once we have done this we will know how are we want to move
1. Collision Detection - Now we know how much Velocity is acting on our object, we can use a raycast to see if it would hit anything if it moved the maximum amount it was allowed to move this frame (`float distance = (Velocities[i] * DeltaTime).magnitude;`). We use Unity's new batch raycasting system, [RaycastCommand](https://docs.unity3d.com/2018.1/Documentation/ScriptReference/RaycastCommand.html). Its built on the Job system and runs our casts over all the available CPU cores, just like the rest of our physics engine! Once we have done this we will know how far its safe to move.
1. Velocity Integration - Now we know how far we can move, we have to integrate it onto our position. We do this in the most simple way possible, adding the velocity scaled by delta time to the position (Euler Integration).
1. Collision Response - Here we can respond if an object actually hits a collider in the world. In this demo, we reflect the velocity so the colliding object bounces. We also dampen the objects velocity to simulate friction. This damping effect is what ultimately brings an object to a stop. When we detect an object has next to no velocity acting on it we mark it as sleeping -so no forces affect it at all and its position is totally stable. Once an object has been stable for 15 updates, we respawn it to keep the simulation going!
1. Rendering - Finally, we calculate a matrix we can send to Unity's renderer to correctly position the physics object on the screen. We use `Graphics.DrawMeshInstanced(...)` to draw all the physics objects because gosh its fast!

All of these steps are implemented as one (or more!) unity jobs and each job in the chain depends on the one that came before it, that's why were passing the [JobHandle](https://docs.unity3d.com/2018.1/Documentation/ScriptReference/Unity.Jobs.JobHandle.html) from each job into the next one we schedule. This tells unity about that dependency, and it won't schedule a job higher up in the chain until the previous job has been totally completed 

### What next?
Well, that's kinda all there is to it in this demo - there is a lot of room for improvement though if you want to use this as a foundation to build your own physics system! Some of these ideas I have already implemented in HyperPhysics and might write about in the future - others are just cool things to experiment with

* Currently there is only one force generator, gravity. Try implementing Springs (google for Hooke's Law!), or bouncy, or maybe even joints and constraints!
* Currently all objects experience the same gravity in the same direction - why not try adding more interesting gravity implementations like gravity attractors, gravity that makes you fall towards the closest tiny planet (like Mario galaxy!). you could even experiment with trigger volumes that slow down all objects that enter them! 
* The simulation size is currently limited to 1023 objects - this is because that is the most `Graphics.DrawMeshInstanced(...)` can draw in a single call. Try drawing in batches so you can break this limit and 
* The part of this system that scales the worst is our dependancy of Unity's RaycastCommand. Try to write your own raycast system (the custom raycaster in HyperPhysics is the main reason its so fast and can handle so many objects).
* We are currently treating our Physics Objects as zero mass particles... they cant even rotate! try adding mass and rotations to the system.

Have fun! love,
[@LotteMakesStuff ![twitter icon](http://i.imgur.com/wWzX9uB.png)](http://www.twitter.com/LotteMakesStuff)

